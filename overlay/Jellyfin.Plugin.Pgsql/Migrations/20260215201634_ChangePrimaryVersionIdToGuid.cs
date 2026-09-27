using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class ChangePrimaryVersionIdToGuid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Upstream rewrites PrimaryVersionId strings to the GUID format here. On
            // PostgreSQL the column already became uuid in 20260113102337, with the same
            // value mapping, so nothing is left to do. The id stays so the migration
            // history matches upstream's.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
