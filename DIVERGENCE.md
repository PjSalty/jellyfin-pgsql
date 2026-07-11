# Divergence from upstream

Everything this repo changes relative to [JPVenson/Jellyfin.Pgsql](https://github.com/JPVenson/Jellyfin.Pgsql) at the ref pinned in `UPSTREAM_REF`. The list is the contract: if it's not here, we didn't change it.

| Piece | What | Drop when |
|---|---|---|
| `patches/0001-cache-add-EF-second-level-cache-packages.patch` | adds EFCoreSecondLevelCacheInterceptor 5.3.13 + its StackExchange.Redis provider to the csproj (publish output, no PrivateAssets) | upstream ships its own second level cache |
| `patches/0002-cache-wire-the-Valkey-second-level-cache-into-the-pr.patch` | registers the cache interceptor in `Initialise` (fail open, off by default) and flushes the cache after `PurgeDatabase` | upstream accepts an equivalent hook |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/ValkeyCacheConfig.cs` | config record: database.xml CustomProviderOptions first, env vars second, defaults last | with patch 0002 |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/SecondLevelCacheFactory.cs` | composes the cache library in a plugin private ServiceCollection; process lifetime singleton; admin connection for flush | with patch 0002 |
| `overlay/Jellyfin.Plugin.Pgsql/Cache/ForwardingLoggerProvider.cs` | forwards cache library logs to the host logger | with patch 0002 |

Ceilings worth knowing:

- The cache library is pinned at 5.3.11, the last line on StackExchange.Redis 2.x: 5.3.12+ requires Microsoft.Extensions 10.x, which collides with the host's 9.0.11 pins (NU1605). Bump the pin together with the server's EF line. The library repo publishes no version tags, so the smoke test is the behavioral guard; if NuGet and reviewed behavior ever diverge, vendor the library source instead.
- The pinned upstream tag ships two migration class names that trip CA1707 under the project's own analyzers; upstream renamed them after the tag. All builds pass `-p:NoWarn=CA1707` until UPSTREAM_REF moves past the rename, then the suppression drops.
- A slow-but-alive cache server is worse than a dead one: the library serializes intercepted commands through one process-wide lock around synchronous cache calls. The 500ms connect/sync timeouts cap that stall; watch cache latency, not just availability.
- `IJellyfinDatabaseProvider` has no stability contract. Every server bump can break the provider ABI; the build against the pinned `Jellyfin.Controller` package is the tripwire.
- Cached tables and excluded tables are policy, not mechanism: see the table in the README. A new server table lands uncached-by-default only if it appears in the exclude list; otherwise it is cached and invalidated on write like everything else.
