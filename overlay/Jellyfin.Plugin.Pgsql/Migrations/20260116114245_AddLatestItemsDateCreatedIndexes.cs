using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddLatestItemsDateCreatedIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserData_UserId",
                table: "UserData");

            migrationBuilder.CreateIndex(
                name: "IX_UserData_UserId_ItemId_LastPlayedDate",
                table: "UserData",
                columns: ["UserId", "ItemId", "LastPlayedDate"]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserData_UserId_ItemId_LastPlayedDate",
                table: "UserData");

            migrationBuilder.CreateIndex(
                name: "IX_UserData_UserId",
                table: "UserData",
                column: "UserId");
        }
    }
}
