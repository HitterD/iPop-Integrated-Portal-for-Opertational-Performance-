using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MeetingKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Meetings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // Backfill (meeting scheduling v3 §1), same rule as MeetingKind.Infer, all rows including cancelled ones.
            // Any "SJA Hall" prefix counts as a room: HallBooking.Normalize rewrote hall locations to the canonical
            // "SJA Hall · {Room}" on save, so such a location only ever meant one of the hall rooms.
            migrationBuilder.Sql("""
                UPDATE "Meetings" SET "Kind"='room' WHERE "Location" ILIKE 'SJA Hall%';
                """);
            migrationBuilder.Sql("""
                UPDATE "Meetings" SET "Kind"='voice' WHERE "Kind" IS NULL AND ("Location" = 'Online · iPop Meeting'
                    OR (COALESCE(TRIM("Location"),'')='' AND EXISTS (SELECT 1 FROM "MeetingAttendees" a WHERE a."MeetingId"="Meetings"."Id")));
                """);
            migrationBuilder.Sql("""
                UPDATE "Meetings" SET "Kind"='inperson' WHERE "Kind" IS NULL AND COALESCE(TRIM("Location"),'')<>'';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Meetings");
        }
    }
}
