using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Add_MediaStream_HdrMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DvProfile",
                table: "MediaStream",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasHdr10Metadata",
                table: "MediaStream",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DvProfile",
                table: "MediaStream");

            migrationBuilder.DropColumn(
                name: "HasHdr10Metadata",
                table: "MediaStream");
        }
    }
}
