using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Reset_LocalPqHlgStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Reset both scan gates so unchanged local PQ/HLG files are probed for DV/HDR10 metadata.
            // Use a timestamp supported by both SQLite and MySQL.
            migrationBuilder.Sql(
                @"UPDATE MediaVersion SET DateUpdated = '1970-01-01 00:00:00'
                  WHERE Id IN (
                      SELECT MF.MediaVersionId
                      FROM MediaFile MF
                      INNER JOIN LibraryFolder LF ON LF.Id = MF.LibraryFolderId
                      INNER JOIN LibraryPath LP ON LP.Id = LF.LibraryPathId
                      INNER JOIN LocalLibrary LL ON LL.Id = LP.LibraryId
                      INNER JOIN MediaStream MS ON MS.MediaVersionId = MF.MediaVersionId
                      WHERE MS.MediaStreamKind = 1
                        AND LOWER(MS.ColorTransfer) IN ('smpte2084', 'arib-std-b67')
                  )");

            migrationBuilder.Sql(
                @"UPDATE LibraryFolder SET Etag = NULL
                  WHERE LibraryPathId IN (
                      SELECT LP.Id FROM LibraryPath LP
                      INNER JOIN LocalLibrary LL ON LL.Id = LP.LibraryId
                  )
                  AND Id IN (
                      SELECT MF.LibraryFolderId
                      FROM MediaFile MF
                      INNER JOIN MediaStream MS ON MS.MediaVersionId = MF.MediaVersionId
                      WHERE MS.MediaStreamKind = 1
                        AND LOWER(MS.ColorTransfer) IN ('smpte2084', 'arib-std-b67')
                  )");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Previous scan timestamps and ETags cannot be restored.
        }
    }
}
