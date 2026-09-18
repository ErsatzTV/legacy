using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Reset_NonMovieCollectionCustomPlaybackOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Collection contains local manual collections; Movie includes movies from every media source.
            migrationBuilder.Sql(
                @"UPDATE Collection SET UseCustomPlaybackOrder = 0
                  WHERE UseCustomPlaybackOrder = 1
                    AND Id IN (
                        SELECT CI.CollectionId
                        FROM CollectionItem CI
                        LEFT JOIN Movie M ON M.Id = CI.MediaItemId
                        WHERE M.Id IS NULL
                    );");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Previous custom playback order settings cannot be recovered.
        }
    }
}
