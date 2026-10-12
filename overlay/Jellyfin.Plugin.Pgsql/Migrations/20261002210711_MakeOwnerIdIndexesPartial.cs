using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class MakeOwnerIdIndexesPartial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems",
                columns: ["ExtraType", "OwnerId"],
                filter: "\"ExtraType\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems",
                column: "OwnerId",
                filter: "\"OwnerId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems",
                columns: ["ExtraType", "OwnerId"]);

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems",
                column: "OwnerId");
        }
    }
}
