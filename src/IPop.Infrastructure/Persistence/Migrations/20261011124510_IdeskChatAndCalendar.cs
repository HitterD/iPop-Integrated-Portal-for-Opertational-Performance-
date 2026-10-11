using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IdeskChatAndCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastMessageAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastMessagePreview",
                table: "Tickets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastMessageSender",
                table: "Tickets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UnreadMessages",
                table: "Tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "VisitEnd",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VisitStart",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "CalendarEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalPeople",
                table: "CalendarEvents",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalUrl",
                table: "CalendarEvents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "CalendarEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IdeskWebhookReceipts",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdeskWebhookReceipts", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_Source_ExternalId_OwnerId",
                table: "CalendarEvents",
                columns: new[] { "Source", "ExternalId", "OwnerId" },
                unique: true,
                filter: "\"Source\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IdeskWebhookReceipts_ReceivedAt",
                table: "IdeskWebhookReceipts",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdeskWebhookReceipts");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_Source_ExternalId_OwnerId",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "LastMessageAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "LastMessagePreview",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "LastMessageSender",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "UnreadMessages",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "VisitEnd",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "VisitStart",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "ExternalPeople",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "ExternalUrl",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "CalendarEvents");
        }
    }
}
