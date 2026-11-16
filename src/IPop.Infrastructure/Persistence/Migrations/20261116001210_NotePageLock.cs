using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotePageLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LockedAt",
                table: "NotePages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LockedById",
                table: "NotePages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotePages_LockedById",
                table: "NotePages",
                column: "LockedById");

            migrationBuilder.AddForeignKey(
                name: "FK_NotePages_Users_LockedById",
                table: "NotePages",
                column: "LockedById",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NotePages_Users_LockedById",
                table: "NotePages");

            migrationBuilder.DropIndex(
                name: "IX_NotePages_LockedById",
                table: "NotePages");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                table: "NotePages");

            migrationBuilder.DropColumn(
                name: "LockedById",
                table: "NotePages");
        }
    }
}
