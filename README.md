# jellyfin-pgsql

[JPVenson/Jellyfin.Pgsql](https://github.com/JPVenson/Jellyfin.Pgsql) plus an EF Core second level cache backed by Valkey (or Redis). PostgreSQL as Jellyfin's database, with repeated queries served from a shared cache instead of hitting the database at all.

This is an overlay build, not a diverged fork: `UPSTREAM_REF` pins the upstream release, `patches/` holds two small patches, `overlay/` holds the new cache code. CI reassembles from pristine upstream on every run, so tracking upstream releases stays a one line change. `DIVERGENCE.md` is the complete list of what's different.

## How the cache works

The plugin registers an [EFCoreSecondLevelCacheInterceptor](https://github.com/VahidN/EFCoreSecondLevelCacheInterceptor) on the same `DbContextOptionsBuilder` it configures for Npgsql, so every pooled context Jellyfin creates is covered. Query results and their table dependency graph live in Valkey; any write to a table (including `ExecuteUpdate`/`ExecuteDelete` bulk operations) evicts every cached query that touched it, immediately and for every connected Jellyfin instance. There's no per instance memory cache, which is what makes the invalidation global.

Fail open by design: if Valkey is down, queries run against PostgreSQL uncached, a warning is logged, and the cache is re-probed every 30 seconds. The cache is a performance tier, never an availability dependency.

Security relevant tables are never cached (default exclude list): `Users`, `Permissions`, `Preferences`, `AccessSchedules`, `Devices`, `DeviceOptions`, `ApiKeys`, plus the insert heavy `ActivityLogs`. Token revocation and permission changes always read database truth.

## Install

Build the plugin (or grab a release zip) and drop it into Jellyfin's plugin folder:

```bash
./build/assemble.sh
dotnet publish upstream/Jellyfin.Plugin.Pgsql/Jellyfin.Plugin.Pgsql.csproj -c Release -p:NoWarn=CA1707 -o /path/to/config/plugins/PostgreSQL
```

(`NoWarn=CA1707`: the pinned upstream tag has two migration class names its own analyzers reject; upstream fixed it after the tag. The suppression drops when `UPSTREAM_REF` moves forward.)

Point Jellyfin at the plugin with `config/database.xml` (see `tests/database.xml` for the exact shape), then configure via environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `POSTGRES_HOST/PORT/DB/USER/PASSWORD` | upstream defaults | PostgreSQL connection (unchanged from upstream) |
| `CACHE_ENABLED` | `false` | master switch; off means the plugin behaves exactly like upstream |
| `VALKEY_HOST` | `localhost` | Valkey or Redis host |
| `VALKEY_PORT` | `6379` | port |
| `VALKEY_PASSWORD` | none | password, if your server requires one |
| `VALKEY_DB` | `0` | logical database index |
| `CACHE_TTL_MINUTES` | `30` | absolute expiration backstop; invalidation on write is the primary mechanism |
| `CACHE_EXCLUDED_TABLES` | see above | comma separated override of the exclude list |
| `CACHE_KEY_PREFIX` | `JF_` | cache key prefix |
| `CACHE_COMPRESSION` | `true` | LZ4 compression of cached payloads |
| `CACHE_DEBUG` | `false` | verbose cache logging |

Every variable can also be set as a `CustomProviderOptions` key/value in `database.xml`; the xml value wins over the environment.

Run Valkey as a cache, not a datastore: `--maxmemory-policy volatile-lru`, persistence off (`--save "" --appendonly no`), and a `--maxmemory` bound. `volatile-lru` matters: the cache's dependency index keys carry no TTL and must not be evicted ahead of the data keys.

## Compatibility

| This repo | Upstream plugin | Jellyfin server | EF Core |
|---|---|---|---|
| main | 10.11.11-1 | 10.11.11 | 9.0.11 |

The plugin version is tied to the server version by upstream. When Jellyfin releases, wait for the matching upstream plugin release, bump `UPSTREAM_REF`, and let CI tell you whether the patches still apply.

## Test

```bash
./build/assemble.sh
dotnet publish upstream/Jellyfin.Plugin.Pgsql/Jellyfin.Plugin.Pgsql.csproj -c Release -p:NoWarn=CA1707 -o upstream/publish
./tests/smoke.sh
```

The smoke test boots the full stack (Jellyfin + PostgreSQL + Valkey) with docker compose, completes the startup wizard, asserts cache hits on repeated browsing, asserts a write is immediately visible after invalidation, and asserts the server keeps serving when Valkey dies mid flight.

## Credits and license

All the real work is JPVenson's: the provider, the migrations, and the plugin database API in Jellyfin itself. This repo only adds the cache layer. GPL-3.0, same as upstream.
