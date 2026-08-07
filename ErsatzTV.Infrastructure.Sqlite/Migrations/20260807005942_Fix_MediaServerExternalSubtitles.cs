using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Fix_MediaServerExternalSubtitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // force a re-scan of media server items that have external subtitles; those subtitles were either
            // deleted outright (jellyfin sends no path) or collapsed into a single row (jellyfin and emby)
            migrationBuilder.Sql(
                """
                UPDATE JellyfinEpisode SET Etag = NULL
                WHERE Id IN (
                    SELECT mv.EpisodeId FROM MediaVersion mv
                    INNER JOIN MediaStream ms ON ms.MediaVersionId = mv.Id
                    WHERE mv.EpisodeId IS NOT NULL AND ms.MediaStreamKind = 5
                );
                """);

            migrationBuilder.Sql(
                """
                UPDATE JellyfinMovie SET Etag = NULL
                WHERE Id IN (
                    SELECT mv.MovieId FROM MediaVersion mv
                    INNER JOIN MediaStream ms ON ms.MediaVersionId = mv.Id
                    WHERE mv.MovieId IS NOT NULL AND ms.MediaStreamKind = 5
                );
                """);

            migrationBuilder.Sql(
                """
                UPDATE EmbyEpisode SET Etag = NULL
                WHERE Id IN (
                    SELECT mv.EpisodeId FROM MediaVersion mv
                    INNER JOIN MediaStream ms ON ms.MediaVersionId = mv.Id
                    WHERE mv.EpisodeId IS NOT NULL AND ms.MediaStreamKind = 5
                );
                """);

            migrationBuilder.Sql(
                """
                UPDATE EmbyMovie SET Etag = NULL
                WHERE Id IN (
                    SELECT mv.MovieId FROM MediaVersion mv
                    INNER JOIN MediaStream ms ON ms.MediaVersionId = mv.Id
                    WHERE mv.MovieId IS NOT NULL AND ms.MediaStreamKind = 5
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
