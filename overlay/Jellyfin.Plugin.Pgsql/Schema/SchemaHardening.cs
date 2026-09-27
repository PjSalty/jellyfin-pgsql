using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.Pgsql.Schema;

/// <summary>
/// Idempotent schema hardening the scheduled optimiser ensures on every run
/// (and, since 12.1, right after a migration batch). Upstream's EF model does
/// not carry these; each was proven live in production first:
///  - IX_BaseItems_latest_path: filter+sort path for the Latest/browse
///    family (images p95 1.04s -&gt; 160ms under k6 load, 2026-07-13).
///  - 12.1 retires our unique (UserId, Kind) guards on Permissions and
///    Preferences: upstream's 20260815063607 deletes the orphan rows and makes
///    its own IX_Permissions_UserId_Kind / IX_Preferences_UserId_Kind unique
///    and unfiltered, so the twins are dropped. They stopped a retry loop that
///    once grew Permissions to 251,568 rows for 11 users; upstream's index now
///    does that.
///  - pg_trgm + trigram GIN on CleanName / lower(OriginalTitle): the search
///    filter's LIKE branches and the relevance ordering's prefix matches
///    (added 2026-08-29; the plain-term strpos branch needs the server-side
///    LIKE patch to benefit).
///  - 2026-08-29 drift reconciliation: the two hand-made indexes the planner
///    uses are codified, five zero-scan indexes (one of them ours) are dropped.
/// CONCURRENTLY keeps a busy library serving while indexes build or drop; Npgsql
/// autocommit raw commands satisfy its no-transaction requirement (the
/// optimiser's VACUUM proves the same property).
/// </summary>
[SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1118", Justification = "long index DDL reads better wrapped")]
public static class SchemaHardening
{
    /// <summary>Gets the DDL the optimiser ensures, in order.</summary>
    public static IReadOnlyList<string> Statements { get; } = new[]
    {
        // Upstream owns unique (UserId, Kind) on both tables since 12.1; the EF
        // migrations create it before the optimiser runs, so uniqueness never lapses.
        "DROP INDEX CONCURRENTLY IF EXISTS \"IX_Permissions_UserId_Kind_unique\"",
        "DROP INDEX CONCURRENTLY IF EXISTS \"IX_Preferences_UserId_Kind_unique\"",

        // Overlaps 12.1's IX_BaseItems_TopParentId_MediaType_IsVirtualItem_DateCreated
        // but is not the same index (IsFolder, DESC); kept until pg_stat_user_indexes
        // shows the planner has stopped using it.
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_BaseItems_latest_path\" ON \"BaseItems\" (\"TopParentId\", \"MediaType\", \"IsFolder\", \"IsVirtualItem\", \"DateCreated\" DESC)",

        // The Latest row groups episodes into series, and EF nests that grouping
        // so postgres recomputes the same per-series aggregate once per group -
        // 54 times per request, each one re-reading the library. Covering the
        // aggregate (index-only, already ordered) took the measured endpoint from
        // 3.4s to 1.9s before the query itself was split.
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_BaseItems_series_latest_cover\" ON \"BaseItems\" (\"TopParentId\", \"MediaType\", \"IsFolder\", \"IsVirtualItem\", \"SeriesName\", \"DateCreated\" DESC) INCLUDE (\"Id\", \"PresentationUniqueKey\")",

        // Live drift reconciled 2026-08-29 from pg_stat_user_indexes (counters
        // never reset). Two hand-made indexes are the ones the planner actually
        // picks and were codified nowhere, so a rebuild from code would lose them:
        // UserId_cover serves every per-row COALESCE(Played) probe index-only
        // (1.12 billion scans, the most used index in the database, EXPLAIN
        // confirmed); Type_SortName shows 56k scans but its consumer was not
        // isolated in pg_stat_statements, so it is codified on the counter alone.
        // 12.1 adds UserData (UserId, ItemId, LastPlayedDate), (UserId, Played,
        // ItemId), (UserId, IsFavorite, ItemId) and BaseItems (Type, TopParentId,
        // SortName); none is the same index, so both stay until the counters
        // show the planner has moved to upstream's.
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_UserData_UserId_cover\" ON \"UserData\" (\"UserId\", \"ItemId\") INCLUDE (\"Played\", \"PlaybackPositionTicks\", \"IsFavorite\")",
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_BaseItems_Type_SortName\" ON \"BaseItems\" (\"Type\", \"SortName\") INCLUDE (\"Id\")",

        // The same counters show five indexes with zero scans over their whole
        // life, four hand-made and one of ours. IX_BaseItems_latest_ordered was
        // meant to let the newest-unwatched probe walk newest-first and stop, but
        // that statement wraps its candidates in GROUP BY PresentationUniqueKey,
        // which forces the full grouped set through the series cover index before
        // the top-N sort, so the ordered partial index is never eligible. Each is
        // write amplification on the two hottest tables and nothing else.
        "DROP INDEX CONCURRENTLY IF EXISTS \"IX_BaseItems_latest_ordered\"",
        "DROP INDEX CONCURRENTLY IF EXISTS \"IX_BaseItems_TopParent_Type_SortName\"",
        "DROP INDEX CONCURRENTLY IF EXISTS \"IX_UserData_UserId_Played\"",
        "DROP INDEX CONCURRENTLY IF EXISTS \"IX_UserData_UserId_Resume\"",
        "DROP INDEX CONCURRENTLY IF EXISTS \"IX_UserData_UserId_IsFavorite\"",

        // Search. The search filter and its relevance ordering reach BaseItems
        // through pattern predicates on CleanName and lower(OriginalTitle):
        // LIKE for wildcard terms and NameContains, a StartsWith (LIKE 'x%')
        // pair for the relevance CASE, and strpos() for plain terms, which is
        // how Npgsql translates string.Contains. Measured 2026-08-29 on this
        // library: /Search/Hints 0.93s alone, 4.8s p50 at 20 concurrent, every
        // query a sequential scan over BaseItems. Trigram GIN indexes make the
        // LIKE and prefix forms index-supported; the strpos form cannot use
        // them until the server emits LIKE for plain terms (jellyfin-fork
        // patch, tracked there). pg_trgm is a trusted extension since PG13, so
        // the database owner creates it without superuser; if the role cannot,
        // the optimiser logs the skipped statement and the two indexes fail
        // closed the same way.
        "CREATE EXTENSION IF NOT EXISTS pg_trgm",
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_BaseItems_CleanName_trgm\" ON \"BaseItems\" USING gin (\"CleanName\" gin_trgm_ops)",
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_BaseItems_OriginalTitle_lower_trgm\" ON \"BaseItems\" USING gin (lower(\"OriginalTitle\") gin_trgm_ops)"
    };
}
