using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddBaseItemNameIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_UserData_UserId_IsFavorite_ItemId",
                table: "UserData",
                columns: ["UserId", "IsFavorite", "ItemId"]);

            migrationBuilder.CreateIndex(
                name: "IX_UserData_UserId_Played_ItemId",
                table: "UserData",
                columns: ["UserId", "Played", "ItemId"]);

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_Name",
                table: "BaseItems",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserData_UserId_IsFavorite_ItemId",
                table: "UserData");

            migrationBuilder.DropIndex(
                name: "IX_UserData_UserId_Played_ItemId",
                table: "UserData");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_Name",
                table: "BaseItems");
        }
    }
}
