using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddPeopleNameLowerIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expression index, so it is not part of the EF model; the statement is
            // upstream's own, which is valid PostgreSQL as written.
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_Peoples_NameLower\" ON \"Peoples\" (lower(\"Name\"));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Peoples_NameLower\";");
        }
    }
}
