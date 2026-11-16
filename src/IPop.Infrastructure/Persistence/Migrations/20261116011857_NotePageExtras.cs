using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotePageExtras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "NoteVersions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "NamedById",
                table: "NoteVersions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NoteTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Emoji = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Accent = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<byte[]>(type: "bytea", nullable: false),
                    Excerpt = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SourcePageId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoteTemplates_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NoteViews",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastViewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteViews", x => new { x.PageId, x.UserId });
                    table.ForeignKey(
                        name: "FK_NoteViews_NotePages_PageId",
                        column: x => x.PageId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NoteViews_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NoteTemplates_OwnerId_CreatedAt",
                table: "NoteTemplates",
                columns: new[] { "OwnerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NoteViews_PageId_LastViewedAt",
                table: "NoteViews",
                columns: new[] { "PageId", "LastViewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NoteViews_UserId",
                table: "NoteViews",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteTemplates");

            migrationBuilder.DropTable(
                name: "NoteViews");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "NoteVersions");

            migrationBuilder.DropColumn(
                name: "NamedById",
                table: "NoteVersions");
        }
    }
}
