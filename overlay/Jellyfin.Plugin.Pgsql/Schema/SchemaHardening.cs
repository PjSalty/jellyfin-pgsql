using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.Pgsql.Schema;

/// <summary>
/// Idempotent schema hardening the scheduled optimiser ensures on every run.
/// Upstream's EF model does not carry these; all three were proven live in
/// production first (2026-07-13):
///  - unique (UserId, Kind) on Permissions/Preferences: a retry loop once
///    grew Permissions to 251,568 rows for 11 users and put the auth-path
///    join at 3.2s per request. The constraint makes regrowth impossible.
///  - IX_BaseItems_latest_path: filter+sort path for the Latest/browse
///    family (images p95 1.04s -&gt; 160ms under k6 load).
/// CONCURRENTLY keeps a busy library serving while indexes build; Npgsql
/// autocommit raw commands satisfy its no-transaction requirement (the
/// optimiser's VACUUM proves the same property).
/// </summary>
[SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1118", Justification = "long index DDL reads better wrapped")]
public static class SchemaHardening
{
    /// <summary>Gets the DDL the optimiser ensures, in order.</summary>
    public static IReadOnlyList<string> Statements { get; } = new[]
    {
        "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Permissions_UserId_Kind_unique\" ON \"Permissions\" (\"UserId\", \"Kind\")",
        "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Preferences_UserId_Kind_unique\" ON \"Preferences\" (\"UserId\", \"Kind\")",
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_BaseItems_latest_path\" ON \"BaseItems\" (\"TopParentId\", \"MediaType\", \"IsFolder\", \"IsVirtualItem\", \"DateCreated\" DESC)"
    };
}
