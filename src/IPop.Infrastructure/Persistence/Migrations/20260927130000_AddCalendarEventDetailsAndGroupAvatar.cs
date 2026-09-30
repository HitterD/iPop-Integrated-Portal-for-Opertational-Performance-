using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarEventDetailsAndGroupAvatar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                table: "ChatRooms",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllDay",
                table: "CalendarEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Link",
                table: "CalendarEvents",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "CalendarEvents",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "CalendarEvents",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderMinutes",
                table: "CalendarEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderSentAt",
                table: "CalendarEvents",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                table: "ChatRooms");

            migrationBuilder.DropColumn(
                name: "AllDay",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "Link",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "ReminderMinutes",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "ReminderSentAt",
                table: "CalendarEvents");
        }
    }
}
