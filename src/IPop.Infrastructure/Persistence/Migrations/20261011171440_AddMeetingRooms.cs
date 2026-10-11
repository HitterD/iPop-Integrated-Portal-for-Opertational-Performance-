using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingRooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HallRooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HallRooms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MeetingRecordings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    EgressId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartedById = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SpoolPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NasPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingRecordings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MeetingRecordingViewers",
                columns: table => new
                {
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingRecordingViewers", x => new { x.RecordingId, x.UserId });
                    table.ForeignKey(
                        name: "FK_MeetingRecordingViewers_MeetingRecordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "MeetingRecordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "HallRooms",
                columns: new[] { "Id", "Capacity", "Key", "Name", "SortOrder" },
                values: new object[,]
                {
                    { new Guid("5a11a000-0000-4000-8000-000000000001"), 20, "robusta", "Robusta", 1 },
                    { new Guid("5a11a000-0000-4000-8000-000000000002"), 20, "arabika", "Arabika", 2 },
                    { new Guid("5a11a000-0000-4000-8000-000000000003"), 20, "kalosi", "Kalosi", 3 },
                    { new Guid("5a11a000-0000-4000-8000-000000000004"), 20, "kapal-api", "Kapal Api", 4 },
                    { new Guid("5a11a000-0000-4000-8000-000000000005"), 20, "toraja", "Toraja", 5 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_HallRooms_Key",
                table: "HallRooms",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingRecordings_OneLivePerRoom",
                table: "MeetingRecordings",
                column: "RoomKey",
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingRecordings_RoomKey_Status",
                table: "MeetingRecordings",
                columns: new[] { "RoomKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingRecordingViewers_UserId",
                table: "MeetingRecordingViewers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HallRooms");

            migrationBuilder.DropTable(
                name: "MeetingRecordingViewers");

            migrationBuilder.DropTable(
                name: "MeetingRecordings");
        }
    }
}
