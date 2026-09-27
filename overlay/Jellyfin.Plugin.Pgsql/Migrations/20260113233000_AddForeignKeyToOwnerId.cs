using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddForeignKeyToOwnerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOriginal",
                table: "MediaStreamInfos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OriginalLanguage",
                table: "BaseItems",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "BaseItems",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "OriginalLanguage",
                value: null);

            // PostgreSQL validates existing rows when the constraint is added, so an
            // OwnerId naming an item that no longer exists is repointed to the
            // placeholder first, as upstream does; CleanupOrphanedExtras later deletes
            // the placeholder's extras.
            migrationBuilder.Sql(
                """
                UPDATE "BaseItems" AS e
                SET "OwnerId" = '00000000-0000-0000-0000-000000000001'
                WHERE e."OwnerId" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "BaseItems" AS o WHERE o."Id" = e."OwnerId");
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_BaseItems_BaseItems_OwnerId",
                table: "BaseItems",
                column: "OwnerId",
                principalTable: "BaseItems",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BaseItems_BaseItems_OwnerId",
                table: "BaseItems");

            migrationBuilder.DropColumn(
                name: "IsOriginal",
                table: "MediaStreamInfos");

            migrationBuilder.DropColumn(
                name: "OriginalLanguage",
                table: "BaseItems");
        }
    }
}
