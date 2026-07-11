# Security

Report vulnerabilities privately through GitHub security advisories on this repository (Security tab, "Report a vulnerability"). Don't open public issues for security reports.

Scope worth knowing when assessing impact: the cache stores Jellyfin database query results (library metadata, watch state) in Valkey. Protect the Valkey instance like the database: password auth, network isolation, no exposure beyond the Jellyfin host. Authentication and permission tables are never cached by default.
