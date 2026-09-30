using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// Data only (no schema change): Meeting.Status had two spellings for a cancelled meeting,
    /// "dibatalkan" and "cancelled". Every writer now uses MeetingStatus.Cancelled, so rewrite
    /// the old rows and drop the unsent reminders / calendar rows that cancel now removes.
    [DbContext(typeof(IPopDbContext))]
    [Migration("20260927150000_UnifyMeetingCancelledStatus")]
    public partial class UnifyMeetingCancelledStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "Meetings" SET "Status" = 'cancelled' WHERE "Status" = 'dibatalkan';""");
            migrationBuilder.Sql("""
                DELETE FROM "MeetingReminders" r
                USING "Meetings" m
                WHERE r."MeetingId" = m."Id" AND m."Status" = 'cancelled' AND r."SentAt" IS NULL;
                """);
            migrationBuilder.Sql("""
                DELETE FROM "CalendarEvents" e
                USING "Meetings" m
                WHERE e."MeetingId" = m."Id" AND m."Status" = 'cancelled';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data cleanup; nothing to undo structurally.
        }
    }
}
