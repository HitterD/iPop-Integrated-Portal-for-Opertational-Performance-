using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddMailSchedulingAndPinning : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "ScheduledAt",
            table: "Mails",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Importance",
            table: "Mails",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "normal");

        migrationBuilder.AddColumn<DateTime>(
            name: "CancelledAt",
            table: "Mails",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsPinned",
            table: "MailRecipients",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateIndex(
            name: "IX_Mails_Status_ScheduledAt",
            table: "Mails",
            columns: new[] { "Status", "ScheduledAt" });

        migrationBuilder.CreateIndex(
            name: "IX_MailRecipients_UserId_Folder_IsPinned_CreatedAt",
            table: "MailRecipients",
            columns: new[] { "UserId", "Folder", "IsPinned", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Mails_Status_ScheduledAt",
            table: "Mails");

        migrationBuilder.DropIndex(
            name: "IX_MailRecipients_UserId_Folder_IsPinned_CreatedAt",
            table: "MailRecipients");

        migrationBuilder.DropColumn(
            name: "ScheduledAt",
            table: "Mails");

        migrationBuilder.DropColumn(
            name: "Importance",
            table: "Mails");

        migrationBuilder.DropColumn(
            name: "CancelledAt",
            table: "Mails");

        migrationBuilder.DropColumn(
            name: "IsPinned",
            table: "MailRecipients");
    }
}
