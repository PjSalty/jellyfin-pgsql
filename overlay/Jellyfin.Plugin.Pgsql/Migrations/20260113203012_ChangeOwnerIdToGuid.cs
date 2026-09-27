using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class ChangeOwnerIdToGuid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Upstream's orphan sweep in PostgreSQL form (ctid for SQLite's rowid, now()
            // for datetime('now')): delete every item whose parent chain is dangling,
            // reattaching its play state to the placeholder item the way
            // ItemPersistenceService does on delete. The temp table is created and
            // dropped inside the migration's transaction, so no session state outlives
            // it on the pooled connection.
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS pg_temp."OrphanedBaseItemIds";
                CREATE TEMPORARY TABLE "OrphanedBaseItemIds" ("Id" uuid NOT NULL PRIMARY KEY);

                INSERT INTO pg_temp."OrphanedBaseItemIds" ("Id")
                WITH RECURSIVE orphan ("Id") AS (
                    SELECT child."Id"
                    FROM "BaseItems" AS child
                    WHERE child."ParentId" IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM "BaseItems" AS parent WHERE parent."Id" = child."ParentId")
                    UNION
                    SELECT descendant."Id"
                    FROM "BaseItems" AS descendant
                    INNER JOIN orphan ON descendant."ParentId" = orphan."Id"
                )
                SELECT "Id" FROM orphan;

                -- The placeholder holds one row per (UserId, CustomDataKey): resolve
                -- collisions before repointing anything.
                DELETE FROM "UserData" AS kept
                WHERE kept."ItemId" = '00000000-0000-0000-0000-000000000001'
                  AND EXISTS (
                      SELECT 1
                      FROM "UserData" AS doomed
                      INNER JOIN pg_temp."OrphanedBaseItemIds" AS o ON o."Id" = doomed."ItemId"
                      WHERE doomed."UserId" = kept."UserId"
                        AND doomed."CustomDataKey" = kept."CustomDataKey");

                DELETE FROM "UserData" AS u
                USING (
                    SELECT ctid, row_number() OVER (PARTITION BY "UserId", "CustomDataKey" ORDER BY ctid) AS rn
                    FROM "UserData"
                    WHERE "ItemId" IN (SELECT "Id" FROM pg_temp."OrphanedBaseItemIds")
                ) AS dup
                WHERE u.ctid = dup.ctid
                  AND dup.rn > 1;

                UPDATE "UserData"
                SET "ItemId" = '00000000-0000-0000-0000-000000000001',
                    "RetentionDate" = now()
                WHERE "ItemId" IN (SELECT "Id" FROM pg_temp."OrphanedBaseItemIds");

                DELETE FROM "LinkedChildren"
                WHERE "ParentId" IN (SELECT "Id" FROM pg_temp."OrphanedBaseItemIds")
                   OR "ChildId" IN (SELECT "Id" FROM pg_temp."OrphanedBaseItemIds");

                DELETE FROM "BaseItems" WHERE "Id" IN (SELECT "Id" FROM pg_temp."OrphanedBaseItemIds");

                DROP TABLE pg_temp."OrphanedBaseItemIds";
                """);

            // Upstream normalises OwnerId here (upper case, invalid, empty and
            // placeholder values to NULL) and adds a BaseItemEntityId column that its
            // next migration drops again. On PostgreSQL the normalisation already
            // happened in 20260113102337 as part of the uuid conversion, and the
            // transient column is never created.
            migrationBuilder.UpdateData(
                table: "BaseItems",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "Name",
                value: "This is a placeholder item for UserData that has been detached from its original item");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_ExtraType",
                table: "BaseItems",
                column: "ExtraType");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems",
                columns: ["ExtraType", "OwnerId"]);

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_TopParentId_IsFolder_IsVirtualItem_DateCreated",
                table: "BaseItems",
                columns: ["TopParentId", "IsFolder", "IsVirtualItem", "DateCreated"]);

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_TopParentId_MediaType_IsVirtualItem_DateCreated",
                table: "BaseItems",
                columns: ["TopParentId", "MediaType", "IsVirtualItem", "DateCreated"]);

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_TopParentId_Type_IsVirtualItem_DateCreated",
                table: "BaseItems",
                columns: ["TopParentId", "Type", "IsVirtualItem", "DateCreated"]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BaseItems_ExtraType",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_TopParentId_IsFolder_IsVirtualItem_DateCreated",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_TopParentId_MediaType_IsVirtualItem_DateCreated",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_TopParentId_Type_IsVirtualItem_DateCreated",
                table: "BaseItems");

            migrationBuilder.UpdateData(
                table: "BaseItems",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "Name",
                value: "This is a placeholder item for UserData that has been detacted from its original item");
        }
    }
}
