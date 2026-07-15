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
                // The timeouts are deliberately LAN-tight: the cache library serializes
                // every intercepted command through one process-wide lock around
                // synchronous cache calls, so a SLOW-but-up Valkey stalls the whole DB
                // pipeline for up to the sync timeout per call. Dead Valkey = fine,
                // slow Valkey = the timeout below caps the damage.
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
            services.AddEFSecondLevelCache(options => options
                .UseStackExchangeRedisCacheProvider(redisOptions, TimeSpan.FromMinutes(config.TtlMinutes), config.Compression)
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

                // 5s re-probe: a request whose reader the interceptor already
                // consumed when the cache died errors until the availability
                // check trips the provider into down mode, so this interval
                // bounds the visible error window of a cache outage. A dead
                // cache afterwards means clean uncached operation.
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

        try
        {
            foreach (var endPoint in connection.GetEndPoints())
            {
                connection.GetServer(endPoint).FlushDatabase(_databaseIndex);
            }

            logger.LogInformation("Flushed the EF second level cache on Valkey");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to flush the EF second level cache, stale entries expire via TTL");
        }
    }
}
