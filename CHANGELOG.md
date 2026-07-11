# Changelog

All notable changes to this project are documented here. Generated with git-cliff from conventional commits.

## [Unreleased]

### Added

- EF Core second level cache on Valkey/Redis for the PostgreSQL provider: global write invalidation, fail open, off by default (`CACHE_ENABLED`).
- Overlay build system: `UPSTREAM_REF` + `patches/` + `overlay/`, reassembled from pristine upstream on every CI run.
- Compose based smoke test covering cache hits, write freshness, and Valkey outage fail open.
