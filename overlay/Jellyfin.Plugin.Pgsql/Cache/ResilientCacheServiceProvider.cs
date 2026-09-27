using System;
using System.Threading;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Pgsql.Cache;

/// <summary>
/// Wraps the Redis cache provider so that writing to the cache can never throw.
///
/// This exists because of a defect in EFCoreSecondLevelCacheInterceptor that we cannot
/// patch: the library is a NuGet binary, the bug is present in every release through
/// master, and the upstream fix (PR 325, "Fix closed DbDataReader issue") was closed
/// unmerged on 2025-12-26, three days before 5.3.7 shipped. Issues 215 and 216 are the
/// same bug, fixed and regressed repeatedly across 4.x and 5.x. Upgrading does not help.
///
/// The defect, in DbCommandInterceptorProcessor.ProcessExecutedCommands:
///
///   1. The reader branch drains the live NpgsqlDataReader through EFDataReaderLoader,
///      whose Dispose calls _dbReader.Close(). The real reader is CLOSED at this point,
///      before any cache I/O happens.
///   2. Only then does it call IEFCacheServiceProvider.InsertValue, which for the Redis
///      provider is synchronous SortedSetAdd plus StringSet, bounded by SyncTimeout.
///   3. Any throw from step 2 is caught, and because UseDbCallsIfCachingProviderIsDown is
///      configured the catch does not rethrow: it returns the ORIGINAL result, which is
///      the reader closed in step 1.
///   4. EF then calls Read() on it and gets
///      System.InvalidOperationException: The reader is closed.
///
/// In production that surfaced as roughly 1040 events per day, every one of them on a
/// cached table and none on an excluded one. It broke playback because
/// /Videos/{id}/{msid}/Attachments/{n} resolves through MediaStreamRepository, so a
/// poisoned reader means a missing embedded font and ASS subtitles that do not render.
///
/// The library logs that exception only when its debug logger is enabled, so the failure
/// was completely silent by construction.
///
/// The fix is to make step 2 incapable of throwing, which makes step 3 unreachable. The
/// rows are already materialised in memory by then, so a failed cache write costs nothing
/// beyond a cache miss on the next identical query.
/// </summary>
public sealed class ResilientCacheServiceProvider : IEFCacheServiceProvider
{
    private readonly EFStackExchangeRedisCacheProvider _inner;
    private readonly ILogger<ResilientCacheServiceProvider> _logger;
    private long _suppressed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResilientCacheServiceProvider"/> class.
    /// </summary>
    /// <param name="cacheSettings">Cache settings, carrying the Redis configuration that
    /// UseStackExchangeRedisCacheProvider stored in AdditionalData.</param>
    /// <param name="debugLogger">The library's debug logger.</param>
    /// <param name="dataSerializer">The configured cache payload serializer.</param>
    /// <param name="logger">Host logger for the suppressed-write warning.</param>
    public ResilientCacheServiceProvider(
        IOptions<EFCoreSecondLevelCacheSettings> cacheSettings,
        IEFDebugLogger debugLogger,
        IEFDataSerializer dataSerializer,
        ILogger<ResilientCacheServiceProvider> logger)
    {
        _inner = new EFStackExchangeRedisCacheProvider(cacheSettings, debugLogger, dataSerializer);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Writes an entry to the cache, swallowing any failure.
    ///
    /// This is the only member that runs AFTER the interceptor has drained and closed the
    /// live NpgsqlDataReader, so it is the only one whose exception can poison a reader.
    /// Every other member either runs before the reader is touched or is called outside
    /// the reader path, and their exceptions are handled safely by the library, so they
    /// delegate straight through and keep their normal failure behaviour.
    /// </summary>
    /// <param name="cacheKey">The computed cache key.</param>
    /// <param name="value">The materialised rows to cache.</param>
    /// <param name="cachePolicy">The expiration policy for this entry.</param>
    public void InsertValue(EFCacheKey cacheKey, EFCachedData? value, EFCachePolicy cachePolicy)
    {
        try
        {
            _inner.InsertValue(cacheKey, value, cachePolicy);
        }
        catch (Exception ex)
        {
            // Logged unconditionally, NOT behind the library's debug flag. This exception
            // used to be invisible, which is why the resulting reader-closed errors went
            // unexplained for months. Warning rather than Error: the query itself
            // succeeded and the caller gets correct data, only the cache write was lost.
            var total = Interlocked.Increment(ref _suppressed);
            _logger.LogWarning(
                ex,
                "EF cache write failed and was suppressed, query results are unaffected. Suppressed writes so far: {Suppressed}",
                total);
        }
    }

    /// <inheritdoc />
    public EFCachedData? GetValue(EFCacheKey cacheKey, EFCachePolicy cachePolicy)
        => _inner.GetValue(cacheKey, cachePolicy);

    /// <inheritdoc />
    public void InvalidateCacheDependencies(EFCacheKey cacheKey)
        => _inner.InvalidateCacheDependencies(cacheKey);

    /// <inheritdoc />
    public void ClearAllCachedEntries()
        => _inner.ClearAllCachedEntries();
}
