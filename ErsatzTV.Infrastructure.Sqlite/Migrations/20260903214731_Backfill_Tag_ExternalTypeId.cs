using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Backfill_Tag_ExternalTypeId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // the order is important; each collection scanner takes its own rows first
            // a row that still has a collection id after that came from a plex label
            migrationBuilder.Sql(
                """
                UPDATE Tag SET ExternalTypeId = 'plex/collection'
                WHERE ExternalTypeId IS NULL
                AND ExternalCollectionId IN (SELECT `Key` FROM PlexCollection);
                """);

            migrationBuilder.Sql(
                """
                UPDATE Tag SET ExternalTypeId = 'emby/collection'
                WHERE ExternalTypeId IS NULL
                AND ExternalCollectionId IN (SELECT ItemId FROM EmbyCollection);
                """);

            migrationBuilder.Sql(
                """
                UPDATE Tag SET ExternalTypeId = 'jellyfin/collection'
                WHERE ExternalTypeId IS NULL
                AND ExternalCollectionId IN (SELECT ItemId FROM JellyfinCollection);
                """);

            migrationBuilder.Sql(
                """
                UPDATE Tag SET ExternalTypeId = 'plex/label'
                WHERE ExternalTypeId IS NULL
                AND ExternalCollectionId IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Tag SET ExternalTypeId = NULL
                WHERE ExternalTypeId IN ('plex/label', 'plex/collection', 'emby/collection', 'jellyfin/collection');
                """);
        }
    }
}
