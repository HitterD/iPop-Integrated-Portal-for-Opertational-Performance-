using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarDelegates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SentById",
                table: "Meetings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RespondedById",
                table: "MeetingAttendees",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "CalendarEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedById",
                table: "CalendarEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CalendarDelegates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarDelegates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarDelegates_Users_DelegateId",
                        column: x => x.DelegateId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarDelegates_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarDelegates_DelegateId",
                table: "CalendarDelegates",
                column: "DelegateId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarDelegates_OwnerId_DelegateId",
                table: "CalendarDelegates",
                columns: new[] { "OwnerId", "DelegateId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarDelegates");

            migrationBuilder.DropColumn(
                name: "SentById",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "RespondedById",
                table: "MeetingAttendees");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "UpdatedById",
                table: "CalendarEvents");
        }
    }
}
