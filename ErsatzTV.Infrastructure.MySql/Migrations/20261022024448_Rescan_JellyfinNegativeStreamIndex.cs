using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Rescan_JellyfinNegativeStreamIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"UPDATE JellyfinMovie SET Etag = NULL WHERE Id IN (
                    SELECT MV.MovieId FROM MediaVersion MV
                    INNER JOIN MediaStream MS ON MS.MediaVersionId = MV.Id
                    WHERE MS.`Index` < 0 AND MV.MovieId IS NOT NULL)");

            migrationBuilder.Sql(
                @"UPDATE JellyfinEpisode SET Etag = NULL WHERE Id IN (
                    SELECT MV.EpisodeId FROM MediaVersion MV
                    INNER JOIN MediaStream MS ON MS.MediaVersionId = MV.Id
                    WHERE MS.`Index` < 0 AND MV.EpisodeId IS NOT NULL)");

            migrationBuilder.Sql(
                @"UPDATE Library SET LastScan = '0001-01-01 00:00:00' WHERE Id IN (SELECT Id FROM JellyfinLibrary)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
