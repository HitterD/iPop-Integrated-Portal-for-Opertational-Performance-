using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NoteTodoDue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NoteTodoDues",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Due = table.Column<DateOnly>(type: "date", nullable: false),
                    Text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Done = table.Column<bool>(type: "boolean", nullable: false),
                    DayBeforeSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DaySentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteTodoDues", x => new { x.PageId, x.ItemKey });
                    table.ForeignKey(
                        name: "FK_NoteTodoDues_NotePages_PageId",
                        column: x => x.PageId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NoteTodoDues_Due",
                table: "NoteTodoDues",
                column: "Due");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteTodoDues");
        }
    }
}
