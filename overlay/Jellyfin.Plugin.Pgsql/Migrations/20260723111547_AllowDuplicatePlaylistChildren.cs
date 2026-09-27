using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class AllowDuplicatePlaylistChildren : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Upstream's backfill in PostgreSQL form (ctid for SQLite's rowid): rows
            // with a NULL SortOrder get their 0-based position within the parent, so
            // they stay unique once SortOrder is part of the primary key.
            migrationBuilder.Sql(
                """
                UPDATE "LinkedChildren" AS lc
                SET "SortOrder" = pos.rn
                FROM (
                    SELECT ctid, row_number() OVER (PARTITION BY "ParentId" ORDER BY ctid) - 1 AS rn
                    FROM "LinkedChildren"
                ) AS pos
                WHERE lc.ctid = pos.ctid
                  AND lc."SortOrder" IS NULL;
                """);

            migrationBuilder.DropPrimaryKey(
                name: "PK_LinkedChildren",
                table: "LinkedChildren");

            migrationBuilder.DropIndex(
                name: "IX_LinkedChildren_ParentId_SortOrder",
                table: "LinkedChildren");

            migrationBuilder.AlterColumn<int>(
                name: "SortOrder",
                table: "LinkedChildren",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_LinkedChildren",
                table: "LinkedChildren",
                columns: ["ParentId", "SortOrder"]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The (ParentId, ChildId) key cannot hold a child twice: keep the first
            // entry per pair, as upstream does. Lossy by nature.
            migrationBuilder.Sql(
                """
                DELETE FROM "LinkedChildren" AS lc
                USING (
                    SELECT ctid, row_number() OVER (PARTITION BY "ParentId", "ChildId" ORDER BY ctid) AS rn
                    FROM "LinkedChildren"
                ) AS dup
                WHERE lc.ctid = dup.ctid
                  AND dup.rn > 1;
                """);

            migrationBuilder.DropPrimaryKey(
                name: "PK_LinkedChildren",
                table: "LinkedChildren");

            migrationBuilder.AlterColumn<int>(
                name: "SortOrder",
                table: "LinkedChildren",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddPrimaryKey(
                name: "PK_LinkedChildren",
                table: "LinkedChildren",
                columns: ["ParentId", "ChildId"]);

            migrationBuilder.CreateIndex(
                name: "IX_LinkedChildren_ParentId_SortOrder",
                table: "LinkedChildren",
                columns: ["ParentId", "SortOrder"]);
        }
    }
}
