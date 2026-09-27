using System;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Pgsql.Cache;

/// <summary>
/// Wraps the Redis cache provider so that writing to the cache can never throw and
/// never stalls a request thread.
///
/// The never-throw half exists because of a defect in EFCoreSecondLevelCacheInterceptor
/// that we cannot patch: the library is a NuGet binary, the bug is present in every
/// release through master, and the upstream fix (PR 325, "Fix closed DbDataReader
/// issue") was closed unmerged on 2025-12-26, three days before 5.3.7 shipped. Issues
/// 215 and 216 are the same bug, fixed and regressed repeatedly across 4.x and 5.x.
/// Upgrading does not help: the steps below are unchanged in 5.5.1.
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
///
/// The never-stall half routes writes and invalidations through
/// <see cref="CacheWriteQueue"/> instead of running them synchronously on the request
/// thread. The provider's synchronous calls (SortedSetAdd per dependency table plus
/// StringSet on insert, the scan-and-delete loop on invalidation) are each bounded
/// only by the 500ms SyncTimeout, and under a request burst they pinned request
/// threads against a slow cache server while the threadpool sat at its floor, taking
/// API p95 to 5s. Queueing makes InsertValue an in-memory channel write that cannot
/// stall and cannot throw, so the closed-reader catch is unreachable by construction
/// rather than merely guarded, and SaveChanges never touches the cache server at all,
/// which also removes the cache as a source of DbUpdateException by removing it from
/// the save path entirely. It additionally moves payload serialization and LZ4
/// compression off the request thread, since the inner provider does both inside
/// InsertValue.
/// </summary>
public sealed class ResilientCacheServiceProvider : IEFCacheServiceProvider
{
    private readonly EFStackExchangeRedisCacheProvider _inner;
    private readonly ILogger<ResilientCacheServiceProvider> _logger;
    private readonly CacheWriteQueue _queue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResilientCacheServiceProvider"/> class.
    /// </summary>
    /// <param name="cacheSettings">Cache settings, carrying the Redis configuration that
    /// UseStackExchangeRedisCacheProvider stored in AdditionalData.</param>
    /// <param name="debugLogger">The library's debug logger.</param>
    /// <param name="dataSerializer">The configured cache payload serializer.</param>
    /// <param name="config">The resolved cache configuration, registered as a singleton
    /// by SecondLevelCacheFactory in the same service collection this provider is
    /// resolved from.</param>
    /// <param name="logger">Host logger for suppressed-write and queue diagnostics.</param>
    public ResilientCacheServiceProvider(
        IOptions<EFCoreSecondLevelCacheSettings> cacheSettings,
        IEFDebugLogger debugLogger,
        IEFDataSerializer dataSerializer,
        ValkeyCacheConfig config,
        ILogger<ResilientCacheServiceProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(config);

        _inner = new EFStackExchangeRedisCacheProvider(cacheSettings, debugLogger, dataSerializer);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // FlushCache is the escalation for a lost or failed invalidation: the set of
        // stale entries is unknown by then, and a full flush is always safe.
        _queue = new CacheWriteQueue(_inner, config.WriteQueueCapacity, logger, () => SecondLevelCacheFactory.FlushCache(logger));
    }

    /// <summary>
    /// Queues a cache write and returns immediately.
    ///
    /// This is the only member that runs AFTER the interceptor has drained and closed
    /// the live NpgsqlDataReader, so it is the only one whose exception can poison a
    /// reader. Enqueueing is an in-memory channel write that drops the oldest queued
    /// operation instead of blocking when full and has no reachable throw; the catch
    /// stays as the last line of defence because this is the one call site where a
    /// throw becomes a closed reader handed to EF.
    /// </summary>
    /// <param name="cacheKey">The computed cache key.</param>
    /// <param name="value">The materialised rows to cache.</param>
    /// <param name="cachePolicy">The expiration policy for this entry.</param>
    public void InsertValue(EFCacheKey cacheKey, EFCachedData? value, EFCachePolicy cachePolicy)
    {
        try
        {
            _queue.EnqueueInsert(cacheKey, value, cachePolicy);
        }
        catch (Exception ex)
        {
            // Logged unconditionally, NOT behind the library's debug flag: this
            // exception used to be invisible, which is why the resulting
            // reader-closed errors went unexplained for months.
            _logger.LogWarning(ex, "EF cache write enqueue failed and was suppressed, query results are unaffected");
        }
    }

    /// <summary>
    /// Returns the cached entry, or a miss when an invalidation for one of the entry's
    /// tables is still queued. The pending gate is what keeps read-your-own-write
    /// intact now that invalidations are asynchronous: without it a client could
    /// write, then re-read a cached result the queued invalidation had not deleted
    /// yet. Reads stay synchronous and their failures still propagate; the library
    /// suppresses them via UseDbCallsIfCachingProviderIsDown and falls back to the
    /// database, safely, because no live reader exists yet at this interception point.
    /// </summary>
    /// <param name="cacheKey">The computed cache key.</param>
    /// <param name="cachePolicy">The expiration policy for this entry.</param>
    /// <returns>The cached entry, or null for a miss.</returns>
    public EFCachedData? GetValue(EFCacheKey cacheKey, EFCachePolicy cachePolicy)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);

        return _queue.HasPendingInvalidation(cacheKey) ? null : _inner.GetValue(cacheKey, cachePolicy);
    }

    /// <summary>
    /// Queues an invalidation and returns immediately, so SaveChanges never waits on
    /// the cache server. Invalidations share one FIFO queue with the writes they
    /// supersede: executed out of order, an invalidation could run ahead of an
    /// already queued stale write for the same table, and that write would then
    /// resurrect just-invalidated data until its TTL.
    /// </summary>
    /// <param name="cacheKey">The invalidation key carrying the written tables.</param>
    public void InvalidateCacheDependencies(EFCacheKey cacheKey)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);

        _queue.EnqueueInvalidation(cacheKey);
    }

    /// <summary>
    /// Clears the whole cache, discarding the queue first: a queued write captured
    /// against pre-clear data would otherwise resurrect an entry right after the
    /// clear, the same hole DiscardQueuedWrites closes for FlushCache. The completion
    /// notification runs only when the clear did not throw, so a failed clear leaves
    /// the discarded invalidations' gates dirty and the queue retries with a flush.
    /// </summary>
    public void ClearAllCachedEntries()
    {
        _queue.DiscardQueued();
        _inner.ClearAllCachedEntries();
        _queue.OnFlushCompleted();
    }

    /// <summary>
    /// Drains every queued write and invalidation without executing them. Called by
    /// SecondLevelCacheFactory.FlushCache before it flushes: queued writes captured
    /// against pre-purge data would otherwise resurrect entries for truncated tables
    /// right after the flush.
    /// </summary>
    internal void DiscardQueuedWrites()
        => _queue.DiscardQueued();

    /// <summary>
    /// Reports that a full cache flush completed, which releases the invalidation
    /// gates whose deletes the flush superseded. Called by
    /// SecondLevelCacheFactory.FlushCache only after FlushDatabase returned.
    /// </summary>
    internal void NotifyCacheFlushed()
        => _queue.OnFlushCompleted();
}
