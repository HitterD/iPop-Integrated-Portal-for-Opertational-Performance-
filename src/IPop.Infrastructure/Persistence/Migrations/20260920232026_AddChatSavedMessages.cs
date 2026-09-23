using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSavedMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TargetUrl sudah ditambahkan manual di beberapa lingkungan (dev/staging) —
            // pakai SQL mentah idempoten agar migrasi tidak gagal saat kolom sudah ada.
            migrationBuilder.Sql("""
                ALTER TABLE "NotificationRequests" ADD COLUMN IF NOT EXISTS "TargetUrl" text;
                """);

            migrationBuilder.CreateTable(
                name: "ChatSavedMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatSavedMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatSavedMessages_ChatMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatSavedMessages_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatSavedMessages_MessageId",
                table: "ChatSavedMessages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSavedMessages_UserId_MessageId",
                table: "ChatSavedMessages",
                columns: new[] { "UserId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatSavedMessages_UserId_SavedAt",
                table: "ChatSavedMessages",
                columns: new[] { "UserId", "SavedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatSavedMessages");

            // TargetUrl dibiarkan — kolom bisa saja sudah ada sebelum migrasi ini.
        }
    }
}
