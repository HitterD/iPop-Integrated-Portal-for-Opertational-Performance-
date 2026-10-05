using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CalendarAccessLadder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotifyShareRequests",
                table: "CalendarSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "CalendarGroupShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: true),
                    Level = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarGroupShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarGroupShares_ChatRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "ChatRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarGroupShares_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarGroupShares_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarGroupShares_DepartmentId",
                table: "CalendarGroupShares",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarGroupShares_OwnerId_DepartmentId_RoomId",
                table: "CalendarGroupShares",
                columns: new[] { "OwnerId", "DepartmentId", "RoomId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarGroupShares_RoomId",
                table: "CalendarGroupShares",
                column: "RoomId");

            // Access rework: "Public" (title visible to everyone) is gone — every event is Normal or Private.
            // The old per-owner default is cleared so each owner sees the one-time "now only busy times" card.
            migrationBuilder.Sql("""UPDATE "CalendarEvents" SET "Visibility" = 'busy' WHERE "Visibility" = 'public';""");
            migrationBuilder.Sql("""UPDATE "Meetings" SET "Visibility" = 'busy' WHERE "Visibility" = 'public';""");
            migrationBuilder.Sql("""UPDATE "CalendarSettings" SET "DefaultVisibility" = 'busy';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarGroupShares");

            migrationBuilder.DropColumn(
                name: "NotifyShareRequests",
                table: "CalendarSettings");
        }
    }
}
