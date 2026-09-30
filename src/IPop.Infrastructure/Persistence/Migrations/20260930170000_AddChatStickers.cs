using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// Hand-written (no Designer): the target model lives in IPopDbContextModelSnapshot.
    [DbContext(typeof(IPopDbContext))]
    [Migration("20260930170000_AddChatStickers")]
    public partial class AddChatStickers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatStickers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageServer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    IsOfficial = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatStickers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatStickers_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserStickers",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StickerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserStickers", x => new { x.UserId, x.StickerId });
                    table.ForeignKey(
                        name: "FK_UserStickers_ChatStickers_StickerId",
                        column: x => x.StickerId,
                        principalTable: "ChatStickers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserStickers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatStickers_CreatedById",
                table: "ChatStickers",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ChatStickers_IsOfficial",
                table: "ChatStickers",
                column: "IsOfficial");

            migrationBuilder.CreateIndex(
                name: "IX_UserStickers_StickerId",
                table: "UserStickers",
                column: "StickerId");

            migrationBuilder.CreateIndex(
                name: "IX_UserStickers_UserId_AddedAt",
                table: "UserStickers",
                columns: new[] { "UserId", "AddedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UserStickers");
            migrationBuilder.DropTable(name: "ChatStickers");
        }
    }
}
