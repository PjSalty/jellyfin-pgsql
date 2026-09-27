# Changelog

All notable changes to this project are documented here. Generated with git-cliff from conventional commits.

## [Unreleased]

### Added

- Jellyfin 12.1 support: net10.0 and EF Core 10.0.11 build, 17 PostgreSQL migrations mirroring upstream's 12.1 schema changes (uuid OwnerId and PrimaryVersionId, the LinkedChildren table, index changes), min/max(uuid) aggregates, and re-apply-safe `Down()` for the migrations 12.1 reverts.
- The migration backup checks the pg_dump major against the server first; the rollback restore is atomic and fails loudly.
- EF Core second level cache on Valkey/Redis for the PostgreSQL provider: global write invalidation, fail open, off by default (`CACHE_ENABLED`).
- Overlay build system: `UPSTREAM_REF` + `patches/` + `overlay/`, reassembled from pristine upstream on every CI run.
- Compose based smoke test covering cache hits, write freshness, and Valkey outage fail open.

### Changed

- Upstream pinned to JPVenson/Jellyfin.Pgsql master `80101b9`; cache library 5.5.1; image on `jellyfin/jellyfin:12.1` with `postgresql-client-18`; smoke test on PostgreSQL 18 and Valkey 9.1 with the MediaBrowser authorization header; the unique (UserId, Kind) hardening indexes retire in favour of upstream's.
