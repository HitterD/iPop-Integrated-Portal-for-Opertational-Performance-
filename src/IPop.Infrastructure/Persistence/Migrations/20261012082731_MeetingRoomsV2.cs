using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MeetingRoomsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoomPassword",
                table: "Meetings",
                type: "character varying(6)",
                maxLength: 6,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MeetingId",
                table: "MeetingRecordings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MeetingChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: true),
                    OffsetSeconds = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingChatMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MeetingRoomGuests",
                columns: table => new
                {
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingRoomGuests", x => new { x.MeetingId, x.UserId });
                    table.ForeignKey(
                        name: "FK_MeetingRoomGuests_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MeetingRoomGuests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingChatMessages_MeetingId_SentAt",
                table: "MeetingChatMessages",
                columns: new[] { "MeetingId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingChatMessages_RecordingId",
                table: "MeetingChatMessages",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingRoomGuests_UserId",
                table: "MeetingRoomGuests",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeetingChatMessages");

            migrationBuilder.DropTable(
                name: "MeetingRoomGuests");

            migrationBuilder.DropColumn(
                name: "RoomPassword",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "MeetingId",
                table: "MeetingRecordings");
        }
    }
}
