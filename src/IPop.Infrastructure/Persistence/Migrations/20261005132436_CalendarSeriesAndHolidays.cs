using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CalendarSeriesAndHolidays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Recurrence",
                table: "CalendarEvents",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SeriesId",
                table: "CalendarEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CompanyHolidays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyHolidays", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_SeriesId",
                table: "CalendarEvents",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyHolidays_Date",
                table: "CompanyHolidays",
                column: "Date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyHolidays");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_SeriesId",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "Recurrence",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "CalendarEvents");
        }
    }
}
