using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMailEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Mails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    IsHtml = table.Column<bool>(type: "boolean", nullable: false),
                    Signature = table.Column<string>(type: "text", nullable: true),
                    InReplyToId = table.Column<Guid>(type: "uuid", nullable: true),
                    ThreadRootId = table.Column<Guid>(type: "uuid", nullable: true),
                    ThreadCount = table.Column<int>(type: "integer", nullable: false),
                    LastThreadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    HasAttachment = table.Column<bool>(type: "boolean", nullable: false),
                    PdfExportRequestedBy = table.Column<string>(type: "text", nullable: true),
                    PdfExportRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Mails_Users_SenderId",
                        column: x => x.SenderId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Mails_Mails_InReplyToId",
                        column: x => x.InReplyToId,
                        principalTable: "Mails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Mails_Mails_ThreadRootId",
                        column: x => x.ThreadRootId,
                        principalTable: "Mails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MailAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    MimeType = table.Column<string>(type: "text", nullable: false),
                    SynologyServer = table.Column<string>(type: "text", nullable: false),
                    SynologyPath = table.Column<string>(type: "text", nullable: false),
                    ThumbnailPath = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailAttachments_Mails_MailId",
                        column: x => x.MailId,
                        principalTable: "Mails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Folder = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsStarred = table.Column<bool>(type: "boolean", nullable: false),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailRecipients_Mails_MailId",
                        column: x => x.MailId,
                        principalTable: "Mails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailRecipients_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Mails_InReplyToId_CreatedAt",
                table: "Mails",
                columns: new[] { "InReplyToId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Mails_ThreadRootId_ThreadCount",
                table: "Mails",
                columns: new[] { "ThreadRootId", "ThreadCount" });

            migrationBuilder.CreateIndex(
                name: "IX_Mails_SenderId_Status_CreatedAt",
                table: "Mails",
                columns: new[] { "SenderId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MailAttachments_MailId",
                table: "MailAttachments",
                column: "MailId");

            migrationBuilder.CreateIndex(
                name: "IX_MailRecipients_DeliveredAt",
                table: "MailRecipients",
                column: "DeliveredAt");

            migrationBuilder.CreateIndex(
                name: "IX_MailRecipients_MailId_UserId",
                table: "MailRecipients",
                columns: new[] { "MailId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailRecipients_UserId_Folder_CreatedAt",
                table: "MailRecipients",
                columns: new[] { "UserId", "Folder", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MailRecipients_UserId_Folder_CreatedAt",
                table: "MailRecipients");

            migrationBuilder.DropIndex(
                name: "IX_MailRecipients_MailId_UserId",
                table: "MailRecipients");

            migrationBuilder.DropIndex(
                name: "IX_MailRecipients_DeliveredAt",
                table: "MailRecipients");

            migrationBuilder.DropIndex(
                name: "IX_MailAttachments_MailId",
                table: "MailAttachments");

            migrationBuilder.DropIndex(
                name: "IX_Mails_SenderId_Status_CreatedAt",
                table: "Mails");

            migrationBuilder.DropIndex(
                name: "IX_Mails_ThreadRootId_ThreadCount",
                table: "Mails");

            migrationBuilder.DropIndex(
                name: "IX_Mails_InReplyToId_CreatedAt",
                table: "Mails");

            migrationBuilder.DropTable(
                name: "MailRecipients");

            migrationBuilder.DropTable(
                name: "MailAttachments");

            migrationBuilder.DropTable(
                name: "Mails");
        }
    }
}
