using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NoteLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NoteLinks",
                columns: table => new
                {
                    FromPageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToPageId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteLinks", x => new { x.FromPageId, x.ToPageId });
                    table.ForeignKey(
                        name: "FK_NoteLinks_NotePages_FromPageId",
                        column: x => x.FromPageId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NoteLinks_NotePages_ToPageId",
                        column: x => x.ToPageId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NoteLinks_ToPageId",
                table: "NoteLinks",
                column: "ToPageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteLinks");
        }
    }
}
