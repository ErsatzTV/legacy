using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Reset_MediaServerHdrStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Media server streams never determined HDR10 metadata; false was a default, not a probe result.
            migrationBuilder.Sql(
                @"UPDATE MediaStream SET HasHdr10Metadata = NULL
                  WHERE MediaVersionId IN (
                      SELECT MV.Id
                      FROM MediaVersion MV
                      INNER JOIN MediaItem MI ON MI.Id = COALESCE(
                          MV.MovieId, MV.EpisodeId, MV.MusicVideoId, MV.OtherVideoId,
                          MV.SongId, MV.ImageId, MV.RemoteStreamId)
                      INNER JOIN LibraryPath LP ON LP.Id = MI.LibraryPathId
                      WHERE LP.LibraryId NOT IN (SELECT Id FROM LocalLibrary)
                  )");

            // Reset item etags so the next scan refreshes statistics for possible DV content.
            // DV profile 5 usually reports no color transfer, so unspecified HEVC/AV1 is included.
            foreach (string table in new[]
                     {
                         "PlexMovie", "PlexEpisode", "PlexOtherVideo",
                         "EmbyMovie", "EmbyEpisode",
                         "JellyfinMovie", "JellyfinEpisode"
                     })
            {
                migrationBuilder.Sql(
                    $@"UPDATE {table} SET Etag = NULL
                      WHERE Id IN (
                          SELECT COALESCE(MV.MovieId, MV.EpisodeId, MV.OtherVideoId)
                          FROM MediaVersion MV
                          INNER JOIN MediaStream MS ON MS.MediaVersionId = MV.Id
                          WHERE MS.MediaStreamKind = 1
                            AND LOWER(MS.Codec) IN ('hevc', 'av1')
                            AND (MS.ColorTransfer IS NULL
                                 OR LOWER(MS.ColorTransfer) IN ('', 'smpte2084', 'arib-std-b67'))
                      )");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Previous HDR10 flags and etags cannot be restored.
        }
    }
}
