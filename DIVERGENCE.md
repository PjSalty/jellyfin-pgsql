# Divergence from upstream

Everything this repo changes relative to [JPVenson/Jellyfin.Pgsql](https://github.com/JPVenson/Jellyfin.Pgsql) at the commit pinned in `UPSTREAM_REF`: `80101b9`, which is the `10.11.11-1` release plus the pg_dump/psql pipe-drain fix (#40), `POSTGRES_COMMAND_TIMEOUT` (#41) and the migration class renames. Upstream has no release for the 12.x server line, so the port to Jellyfin 12.1 lives here. The list is the contract: if it's not here, we didn't change it.

## Patches

Applied in order by `build/assemble.sh`; regenerate them with `git format-patch --zero-commit` from the assembled tree.

| Patch | What | Drop when |
|---|---|---|
| `0001-build-retarget-the-provider-to-Jellyfin-12.1-on-net1.patch` | net10.0; Jellyfin.Controller and Jellyfin.Model 12.1.0; EF Core and Microsoft.Extensions at the host's exact 10.0.11 pins, `PrivateAssets` all so none of them ship; Npgsql and its EF provider 10.0.3; dotnet-ef 10.0.11; `build.yaml` targetAbi 12.1.0.0. The .NET 10 SDK's CA1873 is lowered to Info in the ruleset, as the 12.1 server lowers it | upstream publishes a 12.x release |
| `0002-cache-add-EF-second-level-cache-packages.patch` | EFCoreSecondLevelCacheInterceptor and its StackExchange.Redis provider 5.5.1 in the publish output | upstream ships a second level cache of its own |
| `0003-cache-wire-the-Valkey-second-level-cache-into-the-pr.patch` | registers the cache interceptor in `Initialise` (fail open, off by default) and flushes the cache after `PurgeDatabase` | upstream accepts an equivalent hook |
| `0004-schema-ensure-prod-proven-indexes-during-scheduled-o.patch` | runs `Schema/SchemaHardening.cs` after `VACUUM ANALYZE` in `RunScheduledOptimisation`, one statement at a time, never failing the pass | upstream's EF model carries equivalent indexes |
| `0005-db-warm-the-connection-pool-and-keep-connectors-aliv.patch` | warm pool floor, keepalives and a pool cap, env-tunable | upstream sets sane pool defaults |
| `0006-db-auto-prepare-the-hot-statements-so-postgres-stops.patch` | Npgsql `MaxAutoPrepare`, env-tunable | upstream enables auto-prepare |
| `0007-db-stop-the-7-statement-pool-return-reset-auto-prepa.patch` | `NoResetOnClose`, larger auto-prepare budget | upstream sets sane Npgsql pooling defaults |
| `0008-db-fail-the-migration-restore-loudly-and-atomically.patch` | `RestoreBackupFast` runs psql with `ON_ERROR_STOP`, `--single-transaction` and `--no-psqlrc`; a missing backup throws | upstream restores atomically and fails on a missing backup |
| `0009-db-check-pg_dump-against-the-server-major-before-the.patch` | `MigrationBackupFast` compares the `pg_dump --version` major with the server major before dumping | upstream checks the client version itself |
| `0010-migrations-survive-the-revert-Jellyfin-12.1-runs-ove.patch` | `Down()` of `20260522092303_AddNormalizedUsername` also deletes the `20260522092304_UpdateNormalizedUsername` history row; `Down()` of `20260128200059_10.11.6-1` reverts only the ParentId foreign key | upstream ships equivalent `Down()` changes |
| `0011-migrations-model-snapshot-for-the-Jellyfin-12.1-sche.patch` | the EF model snapshot of the 12.1 model | upstream ships its own 12.x migrations |

## Overlay files

Copied over the assembled tree after the patches.

| File | What | Drop when |
|---|---|---|
| `overlay/Jellyfin.Plugin.Pgsql/Migrations/2026*.cs` | the 17 PostgreSQL migrations for the 12.1 model, each with its Designer target model; see "The 12.1 migrations" | upstream ships its own 12.x migrations |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/ResilientCacheServiceProvider.cs` | wraps the Redis cache provider so `InsertValue` cannot throw and no cache write or invalidation runs on a request thread. The library drains and CLOSES the live `NpgsqlDataReader` before writing to the cache, and its catch then returns that closed reader to EF, so any cache-write failure surfaces as `System.InvalidOperationException: The reader is closed`; queueing makes the write an in-memory channel enqueue with no reachable throw | upstream merges an equivalent of PR 325 (closed unmerged 2025-12-26; the bug is still in 5.5.1) AND makes its cache calls asynchronous to the request |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/CacheWriteQueue.cs` | bounded single-consumer queue (dedicated background thread) that applies cache writes and invalidations off the request path. One FIFO for both so an invalidation can never execute ahead of an already queued stale write and resurrect just-invalidated data; per-table pending gate turns reads into misses until a queued invalidation lands (read-your-own-write holds); overflow drops the oldest operation, and a dropped or failed invalidation escalates to a full cache flush while its tables stay gated until that flush completes. Capacity via `CACHE_WRITE_QUEUE_CAPACITY` (default 1024) | with the row above |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/ValkeyCacheConfig.cs` | config record: database.xml CustomProviderOptions first, env vars second, defaults last | with patch 0003 |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/SecondLevelCacheFactory.cs` | composes the cache library in a plugin private ServiceCollection; process lifetime singleton; admin connection for flush | with patch 0003 |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/ForwardingLoggerProvider.cs` | forwards cache library logs to the host logger | with patch 0003 |
| `overlay/Jellyfin.Plugin.Pgsql/Schema/SchemaHardening.cs` | the index statements patch 0004 applies | with patch 0004 |

## The 12.1 migrations

Seventeen migrations carry a 10.11.11 database to the 12.1 model. Their `[Migration]` ids equal the SQLite ids upstream added between v10.11.11 and v12.1, so the server orders them against its code routines the way it orders upstream's (one ordinal sort per stage, one pending id at a time). Every data step upstream writes in SQLite SQL is ported with quoted identifiers (unquoted names fold to lower case on PostgreSQL and miss the tables), `ctid` where upstream uses `rowid`, and `now()` where it uses `datetime('now')`.

| Id | PostgreSQL content |
|---|---|
| `20260113102337_AddLinkedChildrenTable` | `LinkedChildren` in upstream's first shape (key ParentId+ChildId, nullable SortOrder, five indexes). PostgreSQL only: `OwnerId` and `PrimaryVersionId` become `uuid` here (see below), and `min(uuid)`/`max(uuid)` aggregates are created where missing, because 12.1 picks group representatives with `Min(Id)` and PostgreSQL has no uuid min/max. `Down()` folds the rows back into `BaseItems.Data` and forgets `MigrateLinkedChildren`, as upstream's does |
| `20260113203012_ChangeOwnerIdToGuid` | upstream's recursive orphan sweep (items with a dangling parent chain are deleted; their play state is reattached to the placeholder item, collisions resolved first), the placeholder's corrected name, six indexes. Upstream's OwnerId normalisation already happened in the uuid conversion; its transient `BaseItemEntityId` column, dropped again by the next migration, is never created |
| `20260113233000_AddForeignKeyToOwnerId` | OwnerIds naming a missing item are repointed to the placeholder, then `FK_BaseItems_BaseItems_OwnerId`; `MediaStreamInfos.IsOriginal`, `BaseItems.OriginalLanguage` |
| `20260113233500_DropExtraIdsColumn` | drops `BaseItems.ExtraIds` (a scaffold would have renamed it to `OriginalLanguage` and carried the data over) |
| `20260116114245`, `20260118182305`, `20260130232147`, `20260206224832`, `20260308123920`, `20260504075755`, `20260724185102`, `20260728182152`, `20260812050902` | upstream's index changes as generated for PostgreSQL (the 75-character name of the (Type, SeriesPresentationUniqueKey, ParentIndexNumber, IndexNumber) index is cut to 63 with the `~` Npgsql already uses) |
| `20260215201634_ChangePrimaryVersionIdToGuid` | empty on PostgreSQL: the conversion ran in the first migration. The id stays so the history matches upstream's |
| `20260723111547_AllowDuplicatePlaylistChildren` | NULL SortOrder backfill by position, then the key becomes ParentId+SortOrder |
| `20260728170000_AddPeopleNameLowerIndex` | upstream's expression index statement, valid PostgreSQL as written |
| `20260815063607_RemoveOrphanedUserPermissionsAndPreferences` | deletes Permissions and Preferences rows without a live user, drops the two shadow Guid columns, makes UserId required and (UserId, Kind) unique without a filter. `Down()` recreates the filtered indexes with the PostgreSQL filter the 10.11 migrations used, not the model's `[UserId]` text |

The uuid conversion runs in the first migration rather than at upstream's two later ids: SQLite keeps both columns as TEXT, but the 12.1 entity maps them as `Guid` and Npgsql cannot read text as uuid, so nothing may read `BaseItems` through the 12.1 model before they change. One `ALTER TABLE` rewrites both columns with the value mapping upstream's two migrations apply: a canonical GUID of either case is kept, `PrimaryVersionId` also keeps the 32-hex N form 10.11 wrote, the empty GUID becomes NULL in both columns and the `000..001` placeholder becomes NULL in `OwnerId`. A string that is none of these could not have been parsed by the 12.1 model either and becomes NULL.

Jellyfin 12.1 applies each pending migration with `IMigrator.MigrateAsync(id)`, which first reverts every applied migration whose id sorts after that one. Six mirrored ids sort before `20260128200059_10.11.6-1`, `20260522092303_AddNormalizedUsername` and `20260524120336_AddUniqueNormalizedUsernameIndex`, which every 10.11.11 database has applied, so the first 12.1 step reverts those three and later steps re-apply them in id order. Patch 0010 makes the round trip safe: the NormalizedUsername column comes back empty, so its `Down()` now forgets the backfill routine (which re-runs and refills it before the unique index returns), and the 10.11.6 `Down()` no longer narrows PeopleBaseItemMap's key, which fails once one person holds two roles on one item. The 12.1 code routines that read items (`MigrateLinkedChildren` and the rest) run in the AppInitialisation stage, after every EF migration; only `UpdateNormalizedUsername` and `DisableLegacyAuthorization` share the EF stage.

How it was checked: each Designer target model is the previous one plus its step's model change, and EF's own model differ between consecutive Designer models reproduces each migration's schema operations except the documented hand-SQL replacements (the two uuid `ALTER COLUMN`s and the `Down()` filter text). The last model equals the snapshot, the snapshot equals the 12.1 design-time model, and `dotnet ef migrations add` on top scaffolds an empty migration. A pass shaped like the server's EF stage (one pending id at a time, ordinal order, its two code routines emulated: the NormalizedUsername backfill as the routine does it, the legacy-auth config switch as a no-op) over a restored copy of a 10.11.11 production database (48,127 items, 11 users, PostgreSQL 18.6) applied the 17 migrations, re-applied the three reverted ones and ran both routines in 6.9 s, the longest step 2.9 s; the result has no pending model changes and serves a grouped `Min(Id)` query. On that copy the migrations turned 47,853 empty-GUID OwnerIds into NULL and repointed 141 OwnerIds of deleted items to the placeholder (the `CleanupOrphanedExtras` routine deletes those later); nothing had a dangling parent. The 730 playlist and collection entries embedded in 325 items move to `LinkedChildren` later, in the `MigrateLinkedChildren` routine.

## Ceilings

- The cache library is pinned at 5.5.1 because the plugin's AsyncKeyedLock must EQUAL the server's: the Jellyfin host loads its own AsyncKeyedLock (8.0.2 at 12.1) into the default load context before the plugin, and a higher plugin reference then fails to bind (FileLoadException at cache bootstrap, observed live on the 10.11 host). 5.3.11 through 5.5.1 reference exactly 8.0.2; 5.5.2 needs 8.1.2. Bump only together with the server's own pin. The library repo publishes no version tags, so the smoke test is the behavioral guard; if NuGet and reviewed behavior ever diverge, vendor the library source instead. The Redis provider brings StackExchange.Redis 3.2.1 and MessagePack 3.1.8, above every MessagePack advisory's fixed version (3.1.7), so there is no direct MessagePack pin.
- The .NET 10 SDK's CA1873 (potentially expensive logging) fails upstream's log calls under `AllEnabledByDefault` plus `TreatWarningsAsErrors`; patch 0001 lowers it to Info in the ruleset, the same way the 12.1 server lowers it to a suggestion.
- A slow-but-alive cache server is worse than a dead one, but since the write queue the exposure is reads only: the library runs every cache read synchronously under one lock per interception result type (a constant key, so in effect one lock for all query readers; a waiter proceeds unlocked after the library's 7s lock timeout), and the one synchronous call left on a request thread is `GetValue`. The 500ms connect/sync timeouts cap that stall per read; the queue's consumer eats the same timeouts off the request path, and sustained slowness surfaces as queue overflow (dropped-write log lines) rather than request latency. Watch cache latency, not just availability.
- Since 5.5.0 the library adds 0 to 9 seconds of jitter to every cache TTL, so the 20 second watched-state tier lives 20 to 29 seconds.
- Invalidation is asynchronous, and here is the exact coherence contract. Process-locally it is as-if-synchronous: `EnqueueInvalidation` marks each written table pending before `SaveChanges` returns, and `GetValue` treats a pending table as a miss, so a client that writes then re-reads gets database truth regardless of queue depth. Cross-instance it is eventual with a normally sub-millisecond window (the queue drain), TTL-bounded when the cache server is unhealthy. Cross-thread write/invalidate interleavings were arbitrary under the old synchronous path too (the process-wide lock made each call atomic, not ordered), so the single FIFO queue preserves the ordering guarantee that actually existed, it does not weaken it. The degraded modes keep one invariant: stale entries may exist only while the gate is dirty. A dropped or failed invalidation holds its tables gated (forced misses, never stale reads) until the escalated full flush actually completes, and a failed flush keeps them gated for the retry; the cost of that mode is hit ratio on the affected tables, not correctness. Two TTL-bounded residuals sit outside the gate and are named rather than rounded away: (1) a SELECT that drains pre-write rows but enqueues its cache write after another thread's invalidation already executed, which predates the queue (20s on the UserData tier that takes the write traffic); (2) any cache write in flight or enqueued during a full flush's window (`PurgeDatabase`, `ClearAllCachedEntries`, or the overflow escalation) can land after the flush carrying pre-flush data, so a flush is a strong reset, not an atomic one.
- The closed-reader fix is complete for the cache path, and here is the full accounting rather than a claim. Inside the interceptor's try block, four things run after `EFDataReaderLoader` drains and CLOSES the live `NpgsqlDataReader`, and each would land in the catch that returns that closed reader: (1) `Load()` itself, reachable only if the database dies mid-read, in which case the request fails anyway and only the error message is misleading; (2) `ShouldSkipCachingResults`, which invokes a predicate ONLY when `SkipCachingResults` is non-null, and we never set one, which is why `SecondLevelCacheFactory` carries a do-not-add warning at the exact place someone would add it; (3) `InsertValue`, the one that actually fired in production, now non-throwing via `ResilientCacheServiceProvider`; (4) the `EFTableRowsDataReader` constructor, four field assignments over already-materialised rows. Sites 2 and 4 are unreachable by construction, not by luck, and site 1 is a database failure rather than a cache failure. Re-checked against 5.5.1: the same four sites, in the same try block.
- Fail-open is instant, and the claim that it was merely "eventual" was wrong in a way worth recording. This file used to say the error window was "seconds, not an outage", bounded by the 5s availability re-probe. It was not bounded at all: `EFCacheServiceCheck` probes with a single `GetValue("__Test__")`, which succeeds against a cache that is up but slow or intermittently failing, so down-mode never engaged. Production took roughly 1040 `The reader is closed` events a day, every one on a cached table, and the visible symptom was ASS subtitles failing to render because the embedded-font fetch at `/Videos/{id}/{msid}/Attachments/{n}` resolves through a cached-table query. The smoke test hid it by retrying 12 times and passing if any attempt returned 200. `ResilientCacheServiceProvider` removes the failure path, and the smoke test now asserts the FIRST request succeeds, against both a stopped cache and a paused (slow, still-connected) one.
- `IJellyfinDatabaseProvider` has no stability contract. It did not change between 10.11.11 and 12.1, but every server bump can break the provider ABI; the build against the pinned `Jellyfin.Controller` package is the tripwire.
- Cached tables and excluded tables are policy, not mechanism: see the table in the README. A new server table lands uncached-by-default only if it appears in the exclude list; otherwise it is cached and invalidated on write like everything else. 12.1 adds one table, `LinkedChildren` (playlist and collection membership), classified cached like the other catalogue satellites.
- 12.1 calls `RunScheduledOptimisation` right after every migration batch, `--mode MigrateSystem` included, so `VACUUM ANALYZE` and the hardening statements run at the end of each migration pass, not only on the scheduled task.
- Migrations run under the provider's command timeout (`POSTGRES_COMMAND_TIMEOUT`, default 30 s). The heaviest 12.1 step, the `BaseItems` rewrite for the uuid columns plus its index rebuilds, took under 3 s on a 48k-item library; raise the timeout for the migration pass on libraries an order of magnitude larger.
- The rollback restore (patch 0008) is strict, so the dump must load cleanly into the server it came from. pg_dump 17+ writes `SET transaction_timeout`, which PostgreSQL 16 and older reject: with a client newer than the server the rollback now fails instead of limping through. Keep the client major equal to the server major; patch 0009 refuses an older client and warns about a newer one.

## 0004 schema hardening via the scheduled optimiser

Prod-proven indexes upstream's EF model lacks, starting with
IX_BaseItems_latest_path for the Latest/browse filter+sort family. Until
12.1 the list also created unique (UserId, Kind) on Permissions and
Preferences (regrowth guard for the 251k-row bloat that put the auth join at
3.2s per request); 12.1's 20260815063607 migration deletes the orphan rows
and makes its own IX_Permissions_UserId_Kind / IX_Preferences_UserId_Kind
unique and unfiltered, so the list now drops our `_unique` twins instead.
The EF migrations create upstream's indexes before the optimiser runs, so
uniqueness never lapses. Statements live in
overlay/Jellyfin.Plugin.Pgsql/Schema/SchemaHardening.cs; the patch loops
them in RunScheduledOptimisation with per-statement try/catch. 2026-08-29
adds `pg_trgm` plus trigram GIN indexes on `CleanName` and
`lower(OriginalTitle)`: the search filter's LIKE branches and the relevance
ordering's prefix matches were sequential scans (0.93s alone, 4.8s p50 at
20 concurrent). The plain-term branch is `string.Contains`, which Npgsql
emits as `strpos()`, so it stays a scan until jellyfin-fork makes the server
emit LIKE there; that half is tracked in the fork's DIVERGENCE.

2026-08-29 reconciles the live index set with this file, because the code
is meant to be the source of truth and it was not: `pg_stat_user_indexes`
(counters never reset, `pg_stat_database.stats_reset` is null) showed six
BaseItems/UserData indexes that exist only in the live database, in neither
upstream's EF model snapshot nor here. Two of them are what the planner
actually picks and are now codified: `IX_UserData_UserId_cover` (UserId,
ItemId) INCLUDE (Played, PlaybackPositionTicks, IsFavorite) serves every
per-row `COALESCE((SELECT Played ...))` probe index-only, 1.12 billion scans,
the most used index in the database, EXPLAIN of the grouped count shape
confirms the index-only probe; `IX_BaseItems_Type_SortName` INCLUDE (Id),
56k scans, whose consuming statement was not isolated in pg_stat_statements
(the Series listing that looked likeliest takes the EF
`IX_BaseItems_Type_TopParentId_PresentationUniqueKey` instead), so it is
codified on the counter alone. Five indexes had zero scans for their whole life and are now
dropped with `DROP INDEX CONCURRENTLY IF EXISTS`: the hand-made
`IX_BaseItems_TopParent_Type_SortName`, `IX_UserData_UserId_Played`,
`IX_UserData_UserId_Resume`, `IX_UserData_UserId_IsFavorite`, and our own
`IX_BaseItems_latest_ordered`. That last one claimed the newest-unwatched
probe would walk it newest-first and stop; EXPLAIN of the movies Latest
statement shows the `GROUP BY PresentationUniqueKey` wrapper forces the full
grouped set (1,104 rows) through `IX_BaseItems_series_latest_cover` plus PK
probes before the top-16 sort, so an ordered partial index is never eligible
for that shape. Gain is write amplification only (UserData takes ~5.5k row
writes/day, two thirds non-HOT; BaseItems ~2.4k/day) and ~5.7 MB; the value
is that a rebuild from code now reproduces today's plans and the optimiser
stops re-asserting a dead index every run. The EF index
`IX_UserData_ItemId_UserId_Played` can serve the same (ItemId, UserId) to
Played probe index-only, so losing `UserId_cover` would have been a plan
change rather than a slowdown; it is codified so the plan stays the one that
was measured. Verify after the next optimiser run: `SELECT indexrelname,
idx_scan FROM pg_stat_user_indexes WHERE relname IN ('BaseItems','UserData')`
should list the two codified indexes and none of the five dropped ones.

12.1 adds indexes that overlap three of ours without matching them:
(TopParentId, MediaType, IsVirtualItem, DateCreated) next to latest_path,
UserData (UserId, ItemId, LastPlayedDate), (UserId, Played, ItemId) and
(UserId, IsFavorite, ItemId) next to UserId_cover, and (Type, TopParentId,
SortName) next to Type_SortName. No name or column list collides, so all
stay until pg_stat_user_indexes shows the planner has moved to upstream's.

Ceiling: none, this is durable schema. Drop when: upstream adds equivalent
indexes/constraints to the EF model (offer the migration upstream).

## 0005 connection pool warming

Npgsql defaults `MinPoolSize=0`, so idle pools drain and every request reopens a
physical connection. Each open runs `getaddrinfo`, and under Kubernetes the
parallel A/AAAA lookups hit the conntrack DNAT race that returns EAGAIN; EF's
default execution strategy does not retry the query path, so a failed open
became an HTTP 500 (intermittent 500s on `/Items/*/Images`, missing poster art).
The rate tracked connection-open frequency: idle replica pools took 93 of these
to the always-busy leader's 3. The patch sets, in `GetConnectionBuilder`, a warm
`MinPoolSize` floor (reopens become rare, the load-bearing fix), `KeepAlive` +
`TcpKeepAlive` (conntrack/NAT reaping cannot silently drop pinned connectors),
and a `MaxPoolSize` cap (Npgsql's default 100/pool would threaten Postgres
`max_connections` across replicas). All values read from env
(`POSTGRES_MIN_POOL_SIZE` / `MAX_POOL_SIZE` / `CONN_IDLE_LIFETIME` / `KEEPALIVE`
/ `TCP_KEEPALIVE`) with safe fallbacks, so re-sizing is a manifest edit. Pool
params are orthogonal to the execution strategy, so this never touches the
transaction paths. Ceiling: the pod-scoped DNS mitigations (dnsConfig
single-request-reopen + FQDN host, in the kubernetes deployment) reduce
per-open cost but this reduces open *frequency*, which is the dominant term.
Drop when: upstream ships sane pool defaults or first-class pool config.

## 0006 statement plan reuse (auto-prepare)

Npgsql defaults `MaxAutoPrepare=0`, so every command is parsed and planned from
scratch server-side, and this workload repeats the same handful of EF statements
millions of times (measured live: one session query 747k calls, one `count(*)`
2.6M calls, `pg_prepared_statements` = 0). On the hot user query postgres spent
7.99ms planning against 4.31ms executing. The patch sets `MaxAutoPrepare` (25)
and `AutoPrepareMinUsages` (3) in `GetConnectionBuilder`, both env-tunable
(`POSTGRES_MAX_AUTO_PREPARE`, `POSTGRES_AUTO_PREPARE_MIN_USAGES`). Ceiling:
prepared statements are per-connection server state, bounded at N x MaxPoolSize
(25 x 15 = 375). Postgres still uses custom plans for the first five executions
and adopts a generic plan only when it is not worse, so a skewed query cannot be
pinned to a bad plan. Drop when: upstream enables auto-prepare by default.

## 0007 pool-return reset storm (consequence of 0006)

Enabling `MaxAutoPrepare` in 0006 made Npgsql abandon its single `DISCARD ALL`
on connection return in favour of a SEVEN-statement reset sequence (it must not
blanket-discard, or it would throw away the prepared statements). Measured on
one 100-item TV grid page: 1,081 SQL statements before auto-prepare, 9,289
after. Replaying the reset sequences alone cost 625ms over a unix socket, and
this deployment crosses pod-to-pod TCP at 2.2-2.9ms per connect.
`NoResetOnClose=true` removes 8,120 statements per page and keeps prepared
statements alive across pool churn. Ceiling: a connection reset only matters if
the application leaves session state behind (SET outside a transaction, temp
tables, LISTEN, advisory locks) -- Jellyfin/EF issues plain parameterised DML,
so there is nothing to leak. If a future patch introduces per-session state,
this must be revisited. 0007 also raises MaxAutoPrepare to 100 and lowers
AutoPrepareMinUsages to 2, because pg_stat_statements holds 540 distinct
statements and 25 slots evicts the working set. Drop when: upstream sets sane
Npgsql pooling defaults.

## 0008 atomic, fail-loud migration restore

`RestoreBackupFast` is the only rollback the migration service has on
PostgreSQL: when a migration fails, it replays the pre-migration pg_dump
(`--clean --if-exists`) with psql. psql continues after a failed statement
and exits 0, so a restore that broke half way logged success over a
half-restored database. It now runs with `ON_ERROR_STOP=1`, in one
transaction that rolls back on the first error, and without `~/.psqlrc`; a
missing backup file throws, so the service reports the rollback as failed
(manual intervention) instead of as attempted. Checked against a pg_dump 18
dump of a migrated 12.1 database: the strict restore completes (exit 0), and
a statement failing at the end of the file exits 3 and leaves the database
untouched. Ceiling: see the client/server bullet above; the single
transaction also waits behind any other session holding locks on the tables
it drops, so stop the serving pods before a migration pass. Drop when:
upstream restores atomically.

## 0009 pg_dump version check before the migration backup

Every migration pass starts with a pg_dump backup, and pg_dump refuses a
server of a newer major version, so a client older than the server (for
example postgresql-client-16 against PostgreSQL 18) stopped every migration
with a bare stderr dump before anything ran. `MigrationBackupFast` now reads
the server major over an unpooled connection and the client major from
`pg_dump --version` first: an older client, or no pg_dump at all, throws
with the package to install; a newer client logs a warning (see 0008).
Ceiling: majors only. Drop when: upstream checks the client itself.
