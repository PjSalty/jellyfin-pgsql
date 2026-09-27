# Divergence from upstream

Everything this repo changes relative to [JPVenson/Jellyfin.Pgsql](https://github.com/JPVenson/Jellyfin.Pgsql) at the ref pinned in `UPSTREAM_REF`. The list is the contract: if it's not here, we didn't change it.

| Piece | What | Drop when |
|---|---|---|
| `patches/0001-cache-add-EF-second-level-cache-packages.patch` | adds EFCoreSecondLevelCacheInterceptor 5.3.7 + its StackExchange.Redis provider to the csproj (publish output, no PrivateAssets) | upstream ships its own second level cache |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/ResilientCacheServiceProvider.cs` | wraps the Redis cache provider so `InsertValue` cannot throw. The library drains and CLOSES the live `NpgsqlDataReader` before writing to the cache, and its catch then returns that closed reader to EF, so any cache-write failure surfaces as `System.InvalidOperationException: The reader is closed`. Overlay file, no patch needed | upstream merges an equivalent of PR 325 (closed unmerged 2025-12-26; the bug is still on master) |
| `patches/0002-cache-wire-the-Valkey-second-level-cache-into-the-pr.patch` | registers the cache interceptor in `Initialise` (fail open, off by default) and flushes the cache after `PurgeDatabase` | upstream accepts an equivalent hook |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/ValkeyCacheConfig.cs` | config record: database.xml CustomProviderOptions first, env vars second, defaults last | with patch 0002 |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/SecondLevelCacheFactory.cs` | composes the cache library in a plugin private ServiceCollection; process lifetime singleton; admin connection for flush | with patch 0002 |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/ForwardingLoggerProvider.cs` | forwards cache library logs to the host logger | with patch 0002 |
| `patches/0005-db-auto-prepare-the-hot-statements.patch` | enables Npgsql `MaxAutoPrepare` so postgres caches plans for the handful of EF statements this workload repeats millions of times; env-tunable | upstream enables auto-prepare, or EF/Npgsql change the default |
| `patches/0004-db-warm-the-connection-pool-and-keep-connectors-alive.patch` | warms the Npgsql pool (MinPoolSize floor + keepalive + MaxPoolSize cap) so connections rarely reopen; env-tunable | upstream sets sane pool defaults, or the plugin grows first-class pool config |

Ceilings worth knowing:

- The cache library is pinned at 5.3.7, constrained from both sides. Upward: 5.3.8+ references AsyncKeyedLock 8.x, but the Jellyfin host loads its own AsyncKeyedLock (7.1.8) into the default context before the plugin, and the plugin's higher reference then fails to bind (FileLoadException at cache bootstrap, observed live); the plugin's AsyncKeyedLock must EQUAL the server's pin. Also 5.3.12+ moves StackExchange.Redis to 3.x, which requires Microsoft.Extensions 10.x against the host's 9.0.11 (NU1605). Bump only together with the server's own dependency pins. The library repo publishes no version tags, so the smoke test is the behavioral guard; if NuGet and reviewed behavior ever diverge, vendor the library source instead.
- The pinned upstream tag ships two migration class names that trip CA1707 under the project's own analyzers; upstream renamed them after the tag. All builds pass `-p:NoWarn=CA1707` until UPSTREAM_REF moves past the rename, then the suppression drops.
- A slow-but-alive cache server is worse than a dead one: the library serializes intercepted commands through one process-wide lock around synchronous cache calls. The 500ms connect/sync timeouts cap that stall; watch cache latency, not just availability.
- The closed-reader fix is complete for the cache path, and here is the full accounting rather than a claim. Inside the interceptor's try block, four things run after `EFDataReaderLoader` drains and CLOSES the live `NpgsqlDataReader`, and each would land in the catch that returns that closed reader: (1) `Load()` itself, reachable only if the database dies mid-read, in which case the request fails anyway and only the error message is misleading; (2) `ShouldSkipCachingResults`, which invokes a predicate ONLY when `SkipCachingResults` is non-null, and we never set one, which is why `SecondLevelCacheFactory` carries a do-not-add warning at the exact place someone would add it; (3) `InsertValue`, the one that actually fired in production, now non-throwing via `ResilientCacheServiceProvider`; (4) the `EFTableRowsDataReader` constructor, four field assignments over already-materialised rows. Sites 2 and 4 are unreachable by construction, not by luck, and site 1 is a database failure rather than a cache failure.
- Fail-open is instant, and the claim that it was merely "eventual" was wrong in a way worth recording. This file used to say the error window was "seconds, not an outage", bounded by the 5s availability re-probe. It was not bounded at all: `EFCacheServiceCheck` probes with a single `GetValue("__Test__")`, which succeeds against a cache that is up but slow or intermittently failing, so down-mode never engaged. Production took roughly 1040 `The reader is closed` events a day, every one on a cached table, and the visible symptom was ASS subtitles failing to render because the embedded-font fetch at `/Videos/{id}/{msid}/Attachments/{n}` resolves through a cached-table query. The smoke test hid it by retrying 12 times and passing if any attempt returned 200. `ResilientCacheServiceProvider` removes the failure path, and the smoke test now asserts the FIRST request succeeds, against both a stopped cache and a paused (slow, still-connected) one.
- `IJellyfinDatabaseProvider` has no stability contract. Every server bump can break the provider ABI; the build against the pinned `Jellyfin.Controller` package is the tripwire.
- Cached tables and excluded tables are policy, not mechanism: see the table in the README. A new server table lands uncached-by-default only if it appears in the exclude list; otherwise it is cached and invalidated on write like everything else.

## 0003 schema hardening via the scheduled optimiser

Prod-proven indexes upstream's EF model lacks: unique (UserId, Kind) on
Permissions and Preferences (regrowth guard for the 251k-row bloat that put
the auth join at 3.2s per request) and IX_BaseItems_latest_path for the
Latest/browse filter+sort family. Statements live in
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

Ceiling: none, this is durable schema. Drop when: upstream adds equivalent
indexes/constraints to the EF model (offer the migration upstream).

## 0004 connection pool warming

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

## 0005 statement plan reuse (auto-prepare)

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

## 0006 pool-return reset storm (consequence of 0005)

Enabling `MaxAutoPrepare` in 0005 made Npgsql abandon its single `DISCARD ALL`
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
this must be revisited. 0006 also raises MaxAutoPrepare to 100 and lowers
AutoPrepareMinUsages to 2, because pg_stat_statements holds 540 distinct
statements and 25 slots evicts the working set. Drop when: upstream sets sane
Npgsql pooling defaults.
