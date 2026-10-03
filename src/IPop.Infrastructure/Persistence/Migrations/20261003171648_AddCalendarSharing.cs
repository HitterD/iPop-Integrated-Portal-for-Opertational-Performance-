using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "Meetings",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "busy");

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "CalendarEvents",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "busy");

            migrationBuilder.CreateTable(
                name: "CalendarSettings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefaultVisibility = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    NotifyDelegateChanges = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarSettings", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_CalendarSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalendarSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Color = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarSubscriptions_Users_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarSubscriptions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSubscriptions_TargetUserId",
                table: "CalendarSubscriptions",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSubscriptions_UserId_TargetUserId",
                table: "CalendarSubscriptions",
                columns: new[] { "UserId", "TargetUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarSettings");

            migrationBuilder.DropTable(
                name: "CalendarSubscriptions");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "CalendarEvents");
        }
    }
}
