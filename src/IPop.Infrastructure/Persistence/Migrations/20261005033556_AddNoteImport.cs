using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportKey",
                table: "NotePages",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportSource",
                table: "NotePages",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotePages_OwnerId_ImportKey",
                table: "NotePages",
                columns: new[] { "OwnerId", "ImportKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotePages_OwnerId_ImportKey",
                table: "NotePages");

            migrationBuilder.DropColumn(
                name: "ImportKey",
                table: "NotePages");

            migrationBuilder.DropColumn(
                name: "ImportSource",
                table: "NotePages");
        }
    }
}
