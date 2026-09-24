using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrisRunLogAndSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Log",
                table: "HrisSyncRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Skipped",
                table: "HrisSyncRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TriggeredBy",
                table: "HrisSyncRuns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Log",
                table: "HrisSyncRuns");

            migrationBuilder.DropColumn(
                name: "Skipped",
                table: "HrisSyncRuns");

            migrationBuilder.DropColumn(
                name: "TriggeredBy",
                table: "HrisSyncRuns");
        }
    }
}
