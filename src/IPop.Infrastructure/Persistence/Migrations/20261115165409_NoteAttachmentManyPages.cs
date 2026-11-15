using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NoteAttachmentManyPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_NoteAttachments",
                table: "NoteAttachments");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NoteAttachments",
                table: "NoteAttachments",
                columns: new[] { "AttachmentId", "PageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_NoteAttachments",
                table: "NoteAttachments");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NoteAttachments",
                table: "NoteAttachments",
                column: "AttachmentId");
        }
    }
}
