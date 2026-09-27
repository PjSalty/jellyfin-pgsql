using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Pgsql.Cache;

/// <summary>
/// Applies cache writes and invalidations from a bounded queue on one dedicated
/// background thread, so no request thread ever waits on the cache server.
///
/// Why this exists: the Redis provider's InsertValue is one synchronous SortedSetAdd
/// per dependency table plus a StringSet, and InvalidateCacheDependencies is a
/// SortedSetScan plus KeyDelete loop, each call bounded only by the 500ms SyncTimeout.
/// Under a request burst those synchronous calls pinned request threads against a
/// slow cache server while the threadpool sat at its 8-thread floor (the configured
/// DOTNET_ThreadPool_MinThreads was silently ignored), and API p95 reached 5s with
/// webOS clients timing out. Queueing takes the cache server out of every request
/// path: the only synchronous cache call left on a request thread is the read, whose
/// failure the library already suppresses via UseDbCallsIfCachingProviderIsDown.
///
/// Ordering: writes and invalidations share ONE FIFO queue on purpose. With separate
/// paths an invalidation could execute ahead of an already queued stale write for the
/// same table, and that write would then resurrect just-invalidated data until its
/// TTL (up to 6 hours on the catalogue tier). Single-queue FIFO preserves each
/// thread's program order, which is exactly the guarantee the previous synchronous
/// path gave; cross-thread interleavings were arbitrary before and stay arbitrary,
/// bounded by the TTL backstop either way.
///
/// Read-your-own-write: an invalidation is only asynchronous on the wire, not to
/// this process. Each dependency table is marked pending at enqueue time and
/// released only once its stale entries are provably gone (the delete executed,
/// or a full flush completed), and <see cref="HasPendingInvalidation"/> turns
/// reads for pending tables into cache misses, so a client that writes and
/// immediately re-reads gets database truth even when the queue is backed up.
/// Other Jellyfin instances see the invalidation once it drains, normally within
/// a millisecond; the multi-instance staleness window is the queue latency and is
/// bounded by the TTL backstop when the cache server is unhealthy.
///
/// Overflow: the queue never blocks producers. When full, the oldest operation is
/// dropped: a dropped write costs one future cache miss; a dropped invalidation
/// would mean unbounded staleness, so it escalates to a full cache flush instead.
/// The invariant the gate enforces, in overflow and failure handling alike: stale
/// entries may exist only while the gate is dirty. A dropped or failed
/// invalidation therefore keeps its tables marked pending until a full flush
/// COMPLETES, never merely until one is scheduled: releasing the gate at drop
/// time would let reads hit the still-present stale entries for as long as the
/// escalation takes, which under the very backlog that caused the drop is
/// minutes, not milliseconds. A held gate costs forced misses on those tables,
/// never stale reads, and a failed flush leaves it held for the retry.
/// </summary>
internal sealed class CacheWriteQueue
{
    private readonly Channel<WorkItem> _channel;
    private readonly IEFCacheServiceProvider _inner;
    private readonly ILogger _logger;
    private readonly Action _flushCache;

    // Keyed by the library's root cache key (key prefix + table name), so the entry
    // set is bounded by the table count; counts are zeroed, never removed.
    private readonly ConcurrentDictionary<string, int> _pendingInvalidations = new(StringComparer.Ordinal);

    // Gate releases owed to invalidations that were dropped, failed, or discarded
    // ahead of a full flush. Holds keys only (no payloads), grows with the failure
    // window, and drains on the first flush that completes.
    private readonly ConcurrentQueue<EFCacheKey> _deferredReleases = new();

    private long _droppedWrites;
    private long _droppedInvalidations;
    private long _failedOperations;
    private int _flushPending;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheWriteQueue"/> class and
    /// starts its consumer thread. Never disposed on purpose: the queue must outlive
    /// every pooled context, exactly like the factory that composes the cache.
    /// </summary>
    /// <param name="inner">The provider that performs the actual cache calls.</param>
    /// <param name="capacity">Bounded queue capacity, in queued operations.</param>
    /// <param name="logger">Host logger for drop and failure diagnostics.</param>
    /// <param name="flushCache">Full-flush escalation used when an invalidation is lost or
    /// fails. Must report a completed flush back through <see cref="OnFlushCompleted"/>
    /// (SecondLevelCacheFactory.FlushCache does, via the wrapper); until that call the
    /// flush counts as pending and the gates it owes stay dirty.</param>
    internal CacheWriteQueue(IEFCacheServiceProvider inner, int capacity, ILogger logger, Action flushCache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _flushCache = flushCache ?? throw new ArgumentNullException(nameof(flushCache));
        _channel = Channel.CreateBounded<WorkItem>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,

                // DiscardQueued drains from the purge path, so the consumer
                // thread is not the only reader.
                SingleReader = false,
                SingleWriter = false
            },
            OnItemDropped);

        // A dedicated thread rather than a threadpool task: the consumer performs
        // synchronous cache calls that can block for the full SyncTimeout, and the
        // threadpool-starvation incident this queue exists for is exactly the wrong
        // moment to be competing for a pool thread.
        var consumer = new Thread(ConsumeLoop)
        {
            IsBackground = true,
            Name = "jf-efcache-writer"
        };
        consumer.Start();

        _logger.LogInformation("EF cache write queue started, capacity {Capacity}", capacity);
    }

    private enum WorkKind
    {
        Insert,
        Invalidate
    }

    /// <summary>
    /// Queues a cache write and returns immediately. Never blocks: on overflow the
    /// channel drops its oldest operation instead.
    /// </summary>
    /// <param name="cacheKey">The computed cache key.</param>
    /// <param name="value">The materialised rows to cache.</param>
    /// <param name="cachePolicy">The expiration policy for this entry.</param>
    internal void EnqueueInsert(EFCacheKey cacheKey, EFCachedData? value, EFCachePolicy cachePolicy)
        => _ = _channel.Writer.TryWrite(new WorkItem(WorkKind.Insert, cacheKey, value, cachePolicy));

    /// <summary>
    /// Queues an invalidation and marks its tables pending. Marked BEFORE enqueueing:
    /// a read racing this write must start missing no later than the moment
    /// SaveChanges returns.
    /// </summary>
    /// <param name="cacheKey">The invalidation key carrying the written tables.</param>
    internal void EnqueueInvalidation(EFCacheKey cacheKey)
    {
        foreach (var dependency in cacheKey.CacheDependencies)
        {
            _pendingInvalidations.AddOrUpdate(dependency, 1, static (_, pending) => pending + 1);
        }

        _ = _channel.Writer.TryWrite(new WorkItem(WorkKind.Invalidate, cacheKey, Value: null, Policy: null));
    }

    /// <summary>
    /// Reports whether any of the key's dependency tables has an invalidation that
    /// is queued but not yet executed. Reads treat that as a cache miss, which is
    /// what keeps read-your-own-write intact while invalidations are asynchronous.
    /// </summary>
    /// <param name="cacheKey">The cache key whose dependency tables are checked.</param>
    /// <returns>True when a queued invalidation covers one of the key's tables.</returns>
    internal bool HasPendingInvalidation(EFCacheKey cacheKey)
    {
        foreach (var dependency in cacheKey.CacheDependencies)
        {
            if (_pendingInvalidations.TryGetValue(dependency, out var pending) && pending > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Drains every queued operation without executing it. Used before a full cache
    /// flush: writes captured against pre-purge data would resurrect entries for
    /// truncated tables right after the flush. Not airtight, and honestly so: a write
    /// the consumer is mid-executing right now, or one enqueued between this drain
    /// and the flush completing, can land after the flush. Those entries carry
    /// pre-flush data, are invisible to the pending gate, and expire via their TTL,
    /// which is the bound on the residual window.
    /// </summary>
    internal void DiscardQueued()
    {
        while (_channel.Reader.TryRead(out var item))
        {
            if (item.Kind == WorkKind.Invalidate)
            {
                // The flush that follows supersedes the delete, but the stale
                // entries are still in the cache until it completes, so the gate
                // stays dirty until OnFlushCompleted rather than releasing here.
                DeferRelease(item.CacheKey);
            }
        }
    }

    /// <summary>
    /// Marks the pending full flush as done and releases the gates it owed. Called
    /// after a flush or clear-all has actually completed, never when one is merely
    /// scheduled: every deferred invalidation targeted entries written before the
    /// flush, so a completed flush is proof those entries are gone. The exception is
    /// the mid-flush write residue DiscardQueued documents, which the gate never
    /// covered and TTL bounds.
    /// </summary>
    internal void OnFlushCompleted()
    {
        Volatile.Write(ref _flushPending, 0);
        while (_deferredReleases.TryDequeue(out var cacheKey))
        {
            ReleasePending(cacheKey);
        }
    }

    private void ConsumeLoop()
    {
        var reader = _channel.Reader;
        while (true)
        {
            // Sync-over-async is safe here: this is a dedicated thread with no
            // synchronization context, and blocking it is the whole point.
            if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                return;
            }

            while (reader.TryRead(out var item))
            {
                Execute(item);
            }

            MaybeFlush();
        }
    }

    private void Execute(WorkItem item)
    {
        try
        {
            if (item.Kind == WorkKind.Invalidate)
            {
                _inner.InvalidateCacheDependencies(item.CacheKey);

                // Released only after the delete executed: stale entries may exist
                // only while the gate is dirty, so the failure path defers this
                // release until the escalation flush completes instead.
                ReleasePending(item.CacheKey);
            }
            else if (!HasPendingInvalidation(item.CacheKey))
            {
                // A pending invalidation was enqueued after this write and will
                // delete whatever it stores, so writing it is wasted round trips.
                _inner.InsertValue(item.CacheKey, item.Value, item.Policy!);
            }
        }
        catch (Exception ex)
        {
            if (item.Kind == WorkKind.Invalidate)
            {
                // A failed delete leaves an unknown set of stale entries behind, so
                // the gate stays dirty (forced misses, never stale reads) until the
                // full-flush escalation completes and hands the release back.
                DeferRelease(item.CacheKey);
            }

            var failed = Interlocked.Increment(ref _failedOperations);
            _logger.LogWarning(
                ex,
                "EF cache {Kind} failed in the write queue, query results are unaffected. Failed operations so far: {Failed}",
                item.Kind,
                failed);
        }
    }

    private void MaybeFlush()
    {
        if (Volatile.Read(ref _flushPending) == 0)
        {
            return;
        }

        // The flag is cleared by OnFlushCompleted, not here: a flush only counts
        // once it has actually run, so a failed or swallowed one leaves the flag
        // set and the deferred gates dirty, and the next consumer wakeup retries.
        // Reads on the gated tables are plain misses in the meantime, and TTL
        // remains the backstop while the cache server stays unreachable.
        _flushCache();
    }

    private void DeferRelease(EFCacheKey cacheKey)
    {
        _deferredReleases.Enqueue(cacheKey);
        Volatile.Write(ref _flushPending, 1);
    }

    private void ReleasePending(EFCacheKey cacheKey)
    {
        foreach (var dependency in cacheKey.CacheDependencies)
        {
            _pendingInvalidations.AddOrUpdate(dependency, 0, static (_, pending) => pending > 0 ? pending - 1 : 0);
        }
    }

    private void OnItemDropped(WorkItem item)
    {
        if (item.Kind == WorkKind.Invalidate)
        {
            // The gate is NOT released here. The stale entries this invalidation
            // targeted are still in the cache, and the escalation flush is behind
            // the very backlog that caused the drop, so releasing now would open a
            // stale-read window as long as the flush takes. The tables keep
            // reading as misses until the flush completes.
            DeferRelease(item.CacheKey);
            var dropped = Interlocked.Increment(ref _droppedInvalidations);
            _logger.LogWarning(
                "EF cache invalidation dropped on queue overflow, its tables read as misses until the escalated full flush completes. Dropped invalidations so far: {Dropped}",
                dropped);
        }
        else
        {
            var dropped = Interlocked.Increment(ref _droppedWrites);

            // First drop and every thousandth afterwards: overflow happens in
            // bursts and one line per lost write would flood the log.
            if (dropped == 1 || dropped % 1000 == 0)
            {
                _logger.LogWarning(
                    "EF cache writes dropped on queue overflow, each costs one future cache miss. Dropped writes so far: {Dropped}",
                    dropped);
            }
        }
    }

    private readonly record struct WorkItem(WorkKind Kind, EFCacheKey CacheKey, EFCachedData? Value, EFCachePolicy? Policy);
}
