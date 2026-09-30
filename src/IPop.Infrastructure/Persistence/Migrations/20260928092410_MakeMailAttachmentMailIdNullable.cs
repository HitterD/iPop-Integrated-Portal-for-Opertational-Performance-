using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeMailAttachmentMailIdNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "MailId",
                table: "MailAttachments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "UploaderId",
                table: "MailAttachments",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Pending uploads have no mail to point at, and the old schema cannot hold them.
            migrationBuilder.Sql("""DELETE FROM "MailAttachments" WHERE "MailId" IS NULL;""");

            migrationBuilder.DropColumn(
                name: "UploaderId",
                table: "MailAttachments");

            migrationBuilder.AlterColumn<Guid>(
                name: "MailId",
                table: "MailAttachments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
