using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMassMail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HideRecipients",
                table: "Mails",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMass",
                table: "Mails",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RecipientCount",
                table: "Mails",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RecipientGroupsJson",
                table: "Mails",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SkippedJson",
                table: "Mails",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ViaGroup",
                table: "MailRecipients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MailGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailGroups_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailNotifyJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    DoneAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailNotifyJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MailGroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailGroupMembers", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_MailGroupMembers_MailGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "MailGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailGroupMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailGroupMembers_UserId",
                table: "MailGroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MailGroups_OwnerId",
                table: "MailGroups",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_MailNotifyJobs_DoneAt_NextAttemptAt",
                table: "MailNotifyJobs",
                columns: new[] { "DoneAt", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailGroupMembers");

            migrationBuilder.DropTable(
                name: "MailNotifyJobs");

            migrationBuilder.DropTable(
                name: "MailGroups");

            migrationBuilder.DropColumn(
                name: "HideRecipients",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "IsMass",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "RecipientCount",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "RecipientGroupsJson",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "SkippedJson",
                table: "Mails");

            migrationBuilder.DropColumn(
                name: "ViaGroup",
                table: "MailRecipients");
        }
    }
}
