using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MailReadReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SeenAt",
                table: "MailRecipients",
                type: "timestamp with time zone",
                nullable: true);

            // Read receipts start now (spec 2026-10-14 §3): mail sent earlier reports "not tracked".
            // A re-run keeps the first start. Value = a JSON string in System.Text.Json's UTC format.
            migrationBuilder.Sql("""
                INSERT INTO "AppSettings" ("Key", "ValueJson", "UpdatedAt", "UpdatedByNik")
                VALUES ('mail.receipts.trackedSince',
                        to_jsonb(to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')),
                        now(), NULL)
                ON CONFLICT ("Key") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM "AppSettings" WHERE "Key" = 'mail.receipts.trackedSince';""");

            migrationBuilder.DropColumn(
                name: "SeenAt",
                table: "MailRecipients");
        }
    }
}
