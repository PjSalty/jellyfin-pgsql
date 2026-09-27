using System;
using System.Linq;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Jellyfin.Plugin.Pgsql.Cache;

/// <summary>
/// Composes the EFCoreSecondLevelCacheInterceptor object graph inside a plugin private
/// service collection, so the host application needs no knowledge of the cache library.
/// The interceptor is created once per process and shared by every pooled context, which
/// is safe because the provider Initialise callback only runs inside the single pooled
/// options build.
/// </summary>
public static class SecondLevelCacheFactory
{
    private static readonly object InitLock = new();

    // Held for the process lifetime on purpose: the interceptor and its dependencies
    // must outlive every pooled DbContext. Never disposed.
    private static ServiceProvider? _serviceProvider;
    private static IConnectionMultiplexer? _adminConnection;
    private static int _databaseIndex;

    /// <summary>
    /// Creates the shared cache interceptor on first call and returns the same
    /// instance afterwards. A failure here is the caller's responsibility to catch:
    /// the cache must never break database startup.
    /// </summary>
    /// <param name="config">The resolved cache configuration.</param>
    /// <param name="logger">The host logger cache diagnostics forward to.</param>
    /// <returns>The process wide cache interceptor.</returns>
    public static SecondLevelCacheInterceptor GetOrCreate(ValkeyCacheConfig config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        lock (InitLock)
        {
            if (_serviceProvider is not null)
            {
                return _serviceProvider.GetRequiredService<SecondLevelCacheInterceptor>();
            }

            var redisOptions = new ConfigurationOptions
            {
                Password = config.Password,
                DefaultDatabase = config.Db,

                // Fail open: a dead Valkey makes operations throw fast, the library's
                // availability check marks the provider down and queries run uncached.
                // The timeouts are deliberately LAN-tight: writes and invalidations go
                // through the CacheWriteQueue and never stall a request thread, but
                // cache READS are still synchronous, and the library runs every read
                // under one lock per interception result type (a constant key, so in
                // effect one lock for all query readers; a waiter proceeds unlocked
                // after the library's 7s lock timeout). A SLOW-but-up Valkey therefore
                // stalls the read path for up to the sync timeout per call. Dead
                // Valkey = fine, slow Valkey = the timeout below caps the damage per
                // read, and the queue's consumer eats the same timeout off the
                // request path.
                AbortOnConnectFail = false,
                ConnectTimeout = 500,
                SyncTimeout = 500,

                // Admin access is needed for the FlushCache path used by PurgeDatabase.
                AllowAdmin = true,
                ClientName = "jellyfin-efcache"
            };
            redisOptions.EndPoints.Add(config.Host, config.Port);

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(new ForwardingLoggerProvider(logger)));

            // The library resolves the custom cache provider from this same
            // collection, so registering the config here is what lets
            // ResilientCacheServiceProvider size its write queue.
            services.AddSingleton(config);
            services.AddEFSecondLevelCache(options => options
                .UseStackExchangeRedisCacheProvider(redisOptions, TimeSpan.FromMinutes(config.TtlMinutes), config.Compression)
                // Must come AFTER UseStackExchangeRedisCacheProvider: that call stores the
                // Redis configuration in Settings.AdditionalData (which the inner provider
                // reads) and sets Settings.CacheProvider to the Redis type; this line then
                // repoints CacheProvider at the wrapper. See ResilientCacheServiceProvider
                // for why the wrapper exists: a throwing cache write makes the library hand
                // EF a reader it already closed.
                .UseCustomCacheProvider<ResilientCacheServiceProvider>()
                // Two tiers, because the two kinds of data age completely
                // differently. Catalogue tables (BaseItems and its satellites)
                // only change when a library scan runs, so they are cached for
                // hours and survive playback. Anything naming UserData is
                // watched-state: it is rewritten every few seconds during
                // playback, so it gets a short micro-TTL instead of poisoning
                // the long tier. Caching everything on one TTL is what made the
                // measured hit ratio 17.6%: a single stream's progress writes
                // evicted the entire browse cache six times a minute.
                .CacheAllQueriesExceptContainingTableNames(
                    CacheExpirationMode.Absolute,
                    TimeSpan.FromMinutes(config.CatalogueTtlMinutes),
                    config.ExcludedTables.ToArray())
                // CommandTableNames is already an OrdinalIgnoreCase set. CRUD
                // commands are left alone on purpose: overriding a write's policy
                // is how people accidentally disable invalidation.
                .OverrideCachePolicy(context =>
                    !context.IsCrudCommand
                    && context.CommandTableNames.Contains("UserData")
                        ? new EFCachePolicy()
                            .ExpirationMode(CacheExpirationMode.Absolute)
                            .Timeout(TimeSpan.FromSeconds(config.WatchedStateTtlSeconds))
                        : null)
                .UseCacheKeyPrefix(config.KeyPrefix)

                // DO NOT ADD .SkipCachingResults(...) HERE.
                //
                // It is the one remaining way to reopen the closed-reader bug that
                // ResilientCacheServiceProvider fixes. DbCommandIgnoreCachingProcessor
                // .ShouldSkipCachingResults invokes that predicate only when it is
                // non-null, and the call sits INSIDE the interceptor's try block, AFTER
                // EFDataReaderLoader has drained and closed the live NpgsqlDataReader.
                // A predicate that throws therefore lands in the same catch that returns
                // the closed reader, and EF fails with
                // "System.InvalidOperationException: The reader is closed" exactly as
                // before. Leaving it unset makes that branch return false with no
                // reachable throw, which is the only reason the fix is complete rather
                // than partial. Exclude tables via ExcludedTables instead.

                // Required: without it the library RETHROWS a cache failure and every
                // affected query 500s outright.
                //
                // This used to claim the 5s re-probe "bounds the visible error window of a
                // cache outage". That was wrong, and being wrong confidently is why the
                // resulting errors went unexplained for months. EFCacheServiceCheck probes
                // with a single GetValue("__Test__"), which SUCCEEDS against a cache that is
                // up but slow or intermittently failing, so _isCacheServerAvailable stays
                // true and down-mode never engages. The window was unbounded, not 5s.
                //
                // It is bounded now for a different reason: ResilientCacheServiceProvider
                // makes the write path non-throwing, so the catch that returns a closed
                // reader is unreachable. This setting now only covers a genuinely dead
                // cache, which is what its name suggests.
                .UseDbCallsIfCachingProviderIsDown(TimeSpan.FromSeconds(5))

                // The cacheable event forwards hit/miss/invalidation diagnostics to the
                // host logger at Information; without it, CACHE_DEBUG only produces
                // output when Jellyfin's own log level is already Debug.
                .ConfigureLogging(
                    config.Debug,
                    config.Debug ? logEvent => logger.LogInformation("efcache {EventId}: {Message}", logEvent.EventId, logEvent.Message) : null));

            _serviceProvider = services.BuildServiceProvider();
            _databaseIndex = config.Db;
            _adminConnection = ConnectionMultiplexer.Connect(redisOptions);

            return _serviceProvider.GetRequiredService<SecondLevelCacheInterceptor>();
        }
    }

    /// <summary>
    /// Flushes the cache database. Called after operations the interceptor cannot see,
    /// such as the TRUNCATE statements issued by PurgeDatabase during a backup restore.
    /// A flush failure is logged and swallowed: entries then expire via their TTL.
    /// </summary>
    /// <param name="logger">The logger used to report the flush result.</param>
    public static void FlushCache(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var connection = _adminConnection;
        if (connection is null)
        {
            return;
        }

        // Queued cache writes still in flight were captured against pre-purge data;
        // executed after the flush below they would resurrect entries for tables
        // that no longer hold those rows, so they are discarded first. Any drained
        // invalidation keeps its tables gated (reads miss) until the flush is
        // confirmed complete below.
        var wrapper = _serviceProvider?.GetService<IEFCacheServiceProvider>() as ResilientCacheServiceProvider;
        wrapper?.DiscardQueuedWrites();

        try
        {
            foreach (var endPoint in connection.GetEndPoints())
            {
                connection.GetServer(endPoint).FlushDatabase(_databaseIndex);
            }

            // Only after FlushDatabase returned: a completion reported for a flush
            // that never ran would release the invalidation gates while the stale
            // entries they cover are still in the cache.
            wrapper?.NotifyCacheFlushed();
            logger.LogInformation("Flushed the EF second level cache on Valkey");
        }
        catch (Exception ex)
        {
            // No completion notification: the gated tables keep reading as misses
            // and the write queue's consumer retries the flush; TTL remains the
            // backstop for entries the gate never covered.
            logger.LogWarning(ex, "Failed to flush the EF second level cache, stale entries expire via TTL");
        }
    }
}
