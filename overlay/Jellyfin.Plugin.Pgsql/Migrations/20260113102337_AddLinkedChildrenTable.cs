using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkedChildrenTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PostgreSQL only, hoisted from upstream's 20260113203012 and 20260215201634:
            // SQLite keeps OwnerId and PrimaryVersionId as TEXT, but the 12.1 entity maps
            // both as Guid and Npgsql cannot read text as uuid, so the type change lands
            // in the first 12.1 migration, before anything reads BaseItems through the
            // 12.1 model. Values map the way those two upstream migrations map them: a
            // canonical GUID of either case is kept, PrimaryVersionId also keeps the
            // 32-hex N form 10.11 wrote, the empty GUID becomes NULL in both columns and
            // the 000..001 placeholder becomes NULL in OwnerId. Anything else was a string
            // the 12.1 model could not parse either, so it becomes NULL. One ALTER TABLE,
            // so BaseItems is rewritten once.
            migrationBuilder.Sql(
                """
                ALTER TABLE "BaseItems"
                    ALTER COLUMN "OwnerId" TYPE uuid USING (
                        CASE WHEN "OwnerId" ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                            THEN NULLIF(NULLIF("OwnerId"::uuid, '00000000-0000-0000-0000-000000000000'), '00000000-0000-0000-0000-000000000001')
                        END),
                    ALTER COLUMN "PrimaryVersionId" TYPE uuid USING (
                        CASE WHEN "PrimaryVersionId" ~* '^([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$'
                            THEN NULLIF("PrimaryVersionId"::uuid, '00000000-0000-0000-0000-000000000000')
                        END);
                """);

            migrationBuilder.CreateTable(
                name: "LinkedChildren",
                columns: table => new
                {
                    ParentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildType = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkedChildren", x => new { x.ParentId, x.ChildId });
                    table.ForeignKey(
                        name: "FK_LinkedChildren_BaseItems_ChildId",
                        column: x => x.ChildId,
                        principalTable: "BaseItems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LinkedChildren_BaseItems_ParentId",
                        column: x => x.ParentId,
                        principalTable: "BaseItems",
                        principalColumn: "Id");
                });

            migrationBuilder.UpdateData(
                table: "BaseItems",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                columns: ["OwnerId", "PrimaryVersionId"],
                values: [null, null]);

            migrationBuilder.CreateIndex(
                name: "IX_LinkedChildren_ChildId",
                table: "LinkedChildren",
                column: "ChildId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkedChildren_ChildId_ChildType",
                table: "LinkedChildren",
                columns: ["ChildId", "ChildType"]);

            migrationBuilder.CreateIndex(
                name: "IX_LinkedChildren_ParentId",
                table: "LinkedChildren",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkedChildren_ParentId_ChildType",
                table: "LinkedChildren",
                columns: ["ParentId", "ChildType"]);

            migrationBuilder.CreateIndex(
                name: "IX_LinkedChildren_ParentId_SortOrder",
                table: "LinkedChildren",
                columns: ["ParentId", "SortOrder"]);

            // PostgreSQL only: 12.1 picks a representative item per group with
            // Min(Id) (alternate versions, presentation keys, name lookups) and
            // PostgreSQL has no min()/max() over uuid, where SQLite's min() takes
            // anything. Each aggregate is created only when missing.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regprocedure('min(uuid)') IS NULL THEN
                        CREATE OR REPLACE FUNCTION "jellyfin_uuid_smaller"(uuid, uuid) RETURNS uuid
                            LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
                            AS 'SELECT LEAST($1, $2)';
                        CREATE AGGREGATE "min"(uuid) (
                            SFUNC = "jellyfin_uuid_smaller",
                            STYPE = uuid,
                            COMBINEFUNC = "jellyfin_uuid_smaller",
                            SORTOP = <,
                            PARALLEL = SAFE);
                    END IF;

                    IF to_regprocedure('max(uuid)') IS NULL THEN
                        CREATE OR REPLACE FUNCTION "jellyfin_uuid_larger"(uuid, uuid) RETURNS uuid
                            LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
                            AS 'SELECT GREATEST($1, $2)';
                        CREATE AGGREGATE "max"(uuid) (
                            SFUNC = "jellyfin_uuid_larger",
                            STYPE = uuid,
                            COMBINEFUNC = "jellyfin_uuid_larger",
                            SORTOP = >,
                            PARALLEL = SAFE);
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // As upstream's Down does: fold the rows back into BaseItems.Data before the
            // table goes, and forget MigrateLinkedChildren so a later upgrade moves them
            // out again.
            migrationBuilder.Sql(
                """
                UPDATE "BaseItems" AS b
                SET "Data" = jsonb_set(COALESCE(NULLIF(b."Data", '')::jsonb, '{}'::jsonb), '{LinkedChildren}', lc."Children")::text
                FROM (
                    SELECT l."ParentId",
                           jsonb_agg(
                               jsonb_build_object(
                                   'Path', c."Path",
                                   'Type', CASE l."ChildType" WHEN 1 THEN 'Shortcut' ELSE 'Manual' END,
                                   'ItemId', replace(l."ChildId"::text, '-', ''))
                               ORDER BY l."SortOrder" NULLS FIRST) AS "Children"
                    FROM "LinkedChildren" AS l
                    INNER JOIN "BaseItems" AS c ON c."Id" = l."ChildId"
                    GROUP BY l."ParentId"
                ) AS lc
                WHERE b."Id" = lc."ParentId";
                """);

            migrationBuilder.DropTable(
                name: "LinkedChildren");

            migrationBuilder.Sql("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260113120000_MigrateLinkedChildren';");

            migrationBuilder.Sql(
                """
                DROP AGGREGATE IF EXISTS "min"(uuid);
                DROP AGGREGATE IF EXISTS "max"(uuid);
                DROP FUNCTION IF EXISTS "jellyfin_uuid_smaller"(uuid, uuid);
                DROP FUNCTION IF EXISTS "jellyfin_uuid_larger"(uuid, uuid);
                """);

            // Back to the text forms 10.11 writes: OwnerId as D, PrimaryVersionId as N.
            migrationBuilder.Sql(
                """
                ALTER TABLE "BaseItems"
                    ALTER COLUMN "OwnerId" TYPE text USING "OwnerId"::text,
                    ALTER COLUMN "PrimaryVersionId" TYPE text USING replace("PrimaryVersionId"::text, '-', '');
                """);

            migrationBuilder.UpdateData(
                table: "BaseItems",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                columns: ["OwnerId", "PrimaryVersionId"],
                values: [null, null]);
        }
    }
}
