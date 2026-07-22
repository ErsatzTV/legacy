using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Add_CollectionEnumeratorState_Started : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Started",
                table: "ScheduleItemsEnumeratorState",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Started",
                table: "FillGroupEnumeratorState",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Started",
                table: "CollectionEnumeratorState",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // treat all existing enumerators as started so upgrades never yank an in-progress
            // enumerator to a random start point; only enumerators created after this get a random start
            migrationBuilder.Sql("UPDATE `ScheduleItemsEnumeratorState` SET `Started` = 1");
            migrationBuilder.Sql("UPDATE `FillGroupEnumeratorState` SET `Started` = 1");
            migrationBuilder.Sql("UPDATE `CollectionEnumeratorState` SET `Started` = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Started",
                table: "ScheduleItemsEnumeratorState");

            migrationBuilder.DropColumn(
                name: "Started",
                table: "FillGroupEnumeratorState");

            migrationBuilder.DropColumn(
                name: "Started",
                table: "CollectionEnumeratorState");
        }
    }
}
