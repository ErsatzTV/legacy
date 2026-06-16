using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Fix_CleanupLocalSubtitleCollisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE LibraryFolder SET Etag = NULL
                WHERE Id IN (
                    SELECT LibraryFolderId FROM (
                        SELECT DISTINCT mf.LibraryFolderId
                        FROM Subtitle s
                        INNER JOIN MovieMetadata m ON m.Id = s.MovieMetadataId
                        INNER JOIN MediaVersion mv ON mv.MovieId = m.MovieId
                        INNER JOIN MediaFile mf ON mf.MediaVersionId = mv.Id
                        WHERE mf.LibraryFolderId IS NOT NULL
                          AND (
                            (s.SubtitleKind = 1 AND (s.Path IS NULL OR s.Path = ''))
                            OR EXISTS (
                                SELECT 1 FROM Subtitle s2
                                WHERE s2.MovieMetadataId = s.MovieMetadataId
                                  AND s2.StreamIndex = s.StreamIndex
                                  AND s2.Id <> s.Id
                            )
                          )
                    ) affected
                );
                """);

            migrationBuilder.Sql(
                """
                UPDATE LibraryFolder SET Etag = NULL
                WHERE Id IN (
                    SELECT LibraryFolderId FROM (
                        SELECT DISTINCT mf.LibraryFolderId
                        FROM Subtitle s
                        INNER JOIN EpisodeMetadata m ON m.Id = s.EpisodeMetadataId
                        INNER JOIN MediaVersion mv ON mv.EpisodeId = m.EpisodeId
                        INNER JOIN MediaFile mf ON mf.MediaVersionId = mv.Id
                        WHERE mf.LibraryFolderId IS NOT NULL
                          AND (
                            (s.SubtitleKind = 1 AND (s.Path IS NULL OR s.Path = ''))
                            OR EXISTS (
                                SELECT 1 FROM Subtitle s2
                                WHERE s2.EpisodeMetadataId = s.EpisodeMetadataId
                                  AND s2.StreamIndex = s.StreamIndex
                                  AND s2.Id <> s.Id
                            )
                          )
                    ) affected
                );
                """);

            migrationBuilder.Sql(
                """
                UPDATE LibraryFolder SET Etag = NULL
                WHERE Id IN (
                    SELECT LibraryFolderId FROM (
                        SELECT DISTINCT mf.LibraryFolderId
                        FROM Subtitle s
                        INNER JOIN MusicVideoMetadata m ON m.Id = s.MusicVideoMetadataId
                        INNER JOIN MediaVersion mv ON mv.MusicVideoId = m.MusicVideoId
                        INNER JOIN MediaFile mf ON mf.MediaVersionId = mv.Id
                        WHERE mf.LibraryFolderId IS NOT NULL
                          AND (
                            (s.SubtitleKind = 1 AND (s.Path IS NULL OR s.Path = ''))
                            OR EXISTS (
                                SELECT 1 FROM Subtitle s2
                                WHERE s2.MusicVideoMetadataId = s.MusicVideoMetadataId
                                  AND s2.StreamIndex = s.StreamIndex
                                  AND s2.Id <> s.Id
                            )
                          )
                    ) affected
                );
                """);

            migrationBuilder.Sql(
                """
                UPDATE LibraryFolder SET Etag = NULL
                WHERE Id IN (
                    SELECT LibraryFolderId FROM (
                        SELECT DISTINCT mf.LibraryFolderId
                        FROM Subtitle s
                        INNER JOIN OtherVideoMetadata m ON m.Id = s.OtherVideoMetadataId
                        INNER JOIN MediaVersion mv ON mv.OtherVideoId = m.OtherVideoId
                        INNER JOIN MediaFile mf ON mf.MediaVersionId = mv.Id
                        WHERE mf.LibraryFolderId IS NOT NULL
                          AND (
                            (s.SubtitleKind = 1 AND (s.Path IS NULL OR s.Path = ''))
                            OR EXISTS (
                                SELECT 1 FROM Subtitle s2
                                WHERE s2.OtherVideoMetadataId = s.OtherVideoMetadataId
                                  AND s2.StreamIndex = s.StreamIndex
                                  AND s2.Id <> s.Id
                            )
                          )
                    ) affected
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
