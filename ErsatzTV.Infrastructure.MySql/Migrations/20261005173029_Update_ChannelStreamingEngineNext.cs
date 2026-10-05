using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Update_ChannelStreamingEngineNext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // next only supports TransportStreamHybrid (5) and HttpLiveStreamingSegmenter (4)
            migrationBuilder.Sql(
                "UPDATE Channel SET StreamingEngine = 1, StreamingMode = CASE StreamingMode WHEN 1 THEN 5 WHEN 2 THEN 4 ELSE StreamingMode END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
