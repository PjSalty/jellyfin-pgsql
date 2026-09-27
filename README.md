# jellyfin-pgsql

[![ci](https://github.com/PjSalty/jellyfin-pgsql/actions/workflows/ci.yml/badge.svg)](https://github.com/PjSalty/jellyfin-pgsql/actions/workflows/ci.yml)
[![codeql](https://github.com/PjSalty/jellyfin-pgsql/actions/workflows/codeql.yml/badge.svg)](https://github.com/PjSalty/jellyfin-pgsql/actions/workflows/codeql.yml)
[![release](https://img.shields.io/github/v/release/PjSalty/jellyfin-pgsql)](https://github.com/PjSalty/jellyfin-pgsql/releases/latest)

[JPVenson/Jellyfin.Pgsql](https://github.com/JPVenson/Jellyfin.Pgsql) plus an EF Core second level cache backed by Valkey (or Redis). PostgreSQL as Jellyfin's database, with repeated queries served from a shared cache instead of hitting the database at all.

This is an overlay build, not a diverged fork: `UPSTREAM_REF` pins the upstream release (or, while upstream has none for a server line, a commit), `patches/` holds a short series of small patches, `overlay/` holds the new code: the cache and, for Jellyfin 12.1, the PostgreSQL migrations upstream does not ship yet. CI reassembles from pristine upstream on every run, so tracking upstream stays a one line change. `DIVERGENCE.md` is the complete list of what's different.

## How the cache works

The plugin registers an [EFCoreSecondLevelCacheInterceptor](https://github.com/VahidN/EFCoreSecondLevelCacheInterceptor) on the same `DbContextOptionsBuilder` it configures for Npgsql, so every pooled context Jellyfin creates is covered. Query results and their table dependency graph live in Valkey; any write to a table (including `ExecuteUpdate`/`ExecuteDelete` bulk operations) evicts every cached query that touched it, for every connected Jellyfin instance. Cache writes and evictions are applied from a bounded queue by one background thread per instance, so request threads never wait on Valkey: the writing instance masks its own pending evictions (a client that writes and immediately re-reads gets database truth), other instances see the eviction once the queue drains (normally within a millisecond), and on overflow the oldest queued operation is dropped, with a dropped eviction escalating to a full cache flush while its tables stay masked until that flush lands. There's no per instance memory cache, which is what makes the invalidation global.

Fail open by design, with one honest caveat: when Valkey dies, requests already mid-interception can error until the availability probe marks the cache down (5 second re-probe interval), then everything runs uncached against PostgreSQL with a warning logged. A cache outage costs seconds of errors and then latency, never availability.

Security relevant tables are never cached (default exclude list): `Users`, `Permissions`, `Preferences`, `AccessSchedules`, `Devices`, `DeviceOptions`, `ApiKeys`, plus the insert heavy `ActivityLogs`. Token revocation and permission changes always read database truth.

## Install

Grab the [latest release](https://github.com/PjSalty/jellyfin-pgsql/releases/latest) and unpack it into Jellyfin's plugin folder:

```bash
curl -LO https://github.com/PjSalty/jellyfin-pgsql/releases/download/12.1-0-salty.1/jellyfin-pgsql-12.1-0-salty.1.zip
curl -LO https://github.com/PjSalty/jellyfin-pgsql/releases/download/12.1-0-salty.1/SHA256SUMS
sha256sum -c SHA256SUMS
unzip jellyfin-pgsql-12.1-0-salty.1.zip -d /path/to/config/plugins/PostgreSQL
```

Release tags read `<server>-<upstream plugin release>-salty.<rev>`: `12.1-0-salty.1` is built for Jellyfin 12.1, from a pinned upstream commit because upstream has no 12.x release yet (the `0`), and is this repo's first revision for that pair. The 10.11 line used the same scheme, e.g. `10.11.11-1-salty.1`. Match the server part to your server.

The server needs the PostgreSQL client tools (`pg_dump`, `psql`) at the same major as the database: every migration pass starts with a `pg_dump` backup, `pg_dump` refuses a newer server, and the rollback restore stops on settings an older server does not know. `docker/Dockerfile` installs `postgresql-client-18`.

Or build it yourself and drop the output into the plugin folder:

```bash
./build/assemble.sh
dotnet publish upstream/Jellyfin.Plugin.Pgsql/Jellyfin.Plugin.Pgsql.csproj -c Release -o /path/to/config/plugins/PostgreSQL
```

Building needs the .NET 10 SDK.

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

| This repo | Upstream plugin | Jellyfin server | EF Core | PostgreSQL client |
|---|---|---|---|---|
| main, `12.1-0-salty.N` | master `80101b9` (10.11.11-1 + #40 + #41), 12.1 port in this repo | 12.1 | 10.0.11 | 18 |
| `10.11.11-1-salty.N` | 10.11.11-1 | 10.11.11 | 9.0.11 | 16 |

The plugin version is tied to the server version by upstream. When Jellyfin releases, prefer the matching upstream plugin release; when there is none, pin an upstream commit and carry the server port here, as for 12.1. Bump `UPSTREAM_REF` and let CI tell you whether the patches still apply.

Upgrading a 10.11.11 database to 12.1: the plugin's 12.1 migrations run in the server's normal migration pass, and `DIVERGENCE.md` describes what they do to existing data. Stock 12.1 also carries two code routines that do not run on PostgreSQL yet: `20260911120000_StripEmbeddedLinkedChildren` issues SQLite JSON SQL, and `20260910120000_MigrateRatingLevels` is reported to fail on Npgsql while a reader is still open. An existing database needs a server with both fixed; a fresh install is unaffected, because first-run setup marks those routines applied without running them.

## Test

```bash
./build/assemble.sh
dotnet publish upstream/Jellyfin.Plugin.Pgsql/Jellyfin.Plugin.Pgsql.csproj -c Release -o upstream/publish
./tests/smoke.sh
```

The smoke test boots the full stack (Jellyfin 12.1 + PostgreSQL 18 + Valkey) with docker compose, asserts the first-boot migration backup ran, completes the startup wizard, asserts cache hits on repeated browsing, asserts a write is immediately visible after invalidation, and asserts the server keeps serving when Valkey dies or stalls mid flight. It runs against a fresh database, so it never exercises the 10.11 to 12.1 data migration; that needs a restored copy of a real database.

## Credits and license

All the real work is JPVenson's: the provider, the migrations, and the plugin database API in Jellyfin itself. This repo only adds the cache layer. GPL-3.0, same as upstream.

## Schema drift gate

`policy/table-classification.txt` classifies every table in the EF model as cached or excluded. CI extracts the table list from the assembled model snapshot and fails when the two disagree, so an upstream schema change cannot ship until a human classifies the new table.
