using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class IndexOptimizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaStreamInfos_StreamIndex",
                table: "MediaStreamInfos");

            migrationBuilder.DropIndex(
                name: "IX_MediaStreamInfos_StreamIndex_StreamType",
                table: "MediaStreamInfos");

            migrationBuilder.DropIndex(
                name: "IX_MediaStreamInfos_StreamIndex_StreamType_Language",
                table: "MediaStreamInfos");

            migrationBuilder.DropIndex(
                name: "IX_MediaStreamInfos_StreamType",
                table: "MediaStreamInfos");

            migrationBuilder.DropIndex(
                name: "IX_LinkedChildren_ChildId",
                table: "LinkedChildren");

            migrationBuilder.DropIndex(
                name: "IX_LinkedChildren_ParentId",
                table: "LinkedChildren");

            migrationBuilder.DropIndex(
                name: "IX_Devices_DeviceId",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_ExtraType",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_Id_Type_IsFolder_IsVirtualItem",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItemProviders_ProviderId_ProviderValue_ItemId",
                table: "BaseItemProviders");

            migrationBuilder.DropIndex(
                name: "IX_BaseItemImageInfos_ItemId",
                table: "BaseItemImageInfos");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_SeasonId",
                table: "BaseItems",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_SeriesId",
                table: "BaseItems",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_SeriesName",
                table: "BaseItems",
                column: "SeriesName");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_Type_SeriesPresentationUniqueKey_ParentIndexNumbe~",
                table: "BaseItems",
                columns: ["Type", "SeriesPresentationUniqueKey", "ParentIndexNumber", "IndexNumber"]);

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_Type_TopParentId_SortName",
                table: "BaseItems",
                columns: ["Type", "TopParentId", "SortName"]);

            migrationBuilder.CreateIndex(
                name: "IX_BaseItemProviders_ProviderId_ItemId_ProviderValue",
                table: "BaseItemProviders",
                columns: ["ProviderId", "ItemId", "ProviderValue"]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BaseItems_SeasonId",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_SeriesId",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_SeriesName",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_Type_SeriesPresentationUniqueKey_ParentIndexNumbe~",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_Type_TopParentId_SortName",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItemProviders_ProviderId_ItemId_ProviderValue",
                table: "BaseItemProviders");

            migrationBuilder.CreateIndex(
                name: "IX_MediaStreamInfos_StreamIndex",
                table: "MediaStreamInfos",
                column: "StreamIndex");

            migrationBuilder.CreateIndex(
                name: "IX_MediaStreamInfos_StreamIndex_StreamType",
                table: "MediaStreamInfos",
                columns: ["StreamIndex", "StreamType"]);

            migrationBuilder.CreateIndex(
                name: "IX_MediaStreamInfos_StreamIndex_StreamType_Language",
                table: "MediaStreamInfos",
                columns: ["StreamIndex", "StreamType", "Language"]);

            migrationBuilder.CreateIndex(
                name: "IX_MediaStreamInfos_StreamType",
                table: "MediaStreamInfos",
                column: "StreamType");

            migrationBuilder.CreateIndex(
                name: "IX_LinkedChildren_ChildId",
                table: "LinkedChildren",
                column: "ChildId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkedChildren_ParentId",
                table: "LinkedChildren",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_DeviceId",
                table: "Devices",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_ExtraType",
                table: "BaseItems",
                column: "ExtraType");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_Id_Type_IsFolder_IsVirtualItem",
                table: "BaseItems",
                columns: ["Id", "Type", "IsFolder", "IsVirtualItem"]);

            migrationBuilder.CreateIndex(
                name: "IX_BaseItemProviders_ProviderId_ProviderValue_ItemId",
                table: "BaseItemProviders",
                columns: ["ProviderId", "ProviderValue", "ItemId"]);

            migrationBuilder.CreateIndex(
                name: "IX_BaseItemImageInfos_ItemId",
                table: "BaseItemImageInfos",
                column: "ItemId");
        }
    }
}
