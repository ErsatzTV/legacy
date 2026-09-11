using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Reset_PlexOtherVideoFolderTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE PlexOtherVideo SET Etag = NULL;");
            migrationBuilder.Sql(
                @"UPDATE Library SET LastScan = '1970-01-01 00:00:00'
                  WHERE MediaKind = 4 AND Id IN (SELECT Id FROM PlexLibrary);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
