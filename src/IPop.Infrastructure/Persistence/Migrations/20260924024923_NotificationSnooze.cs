using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationSnooze : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationDeliveries_UserId",
                table: "NotificationDeliveries");

            migrationBuilder.AddColumn<DateTime>(
                name: "SnoozedUntil",
                table: "NotificationDeliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_UserId_SnoozedUntil",
                table: "NotificationDeliveries",
                columns: new[] { "UserId", "SnoozedUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationDeliveries_UserId_SnoozedUntil",
                table: "NotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "SnoozedUntil",
                table: "NotificationDeliveries");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_UserId",
                table: "NotificationDeliveries",
                column: "UserId");
        }
    }
}
