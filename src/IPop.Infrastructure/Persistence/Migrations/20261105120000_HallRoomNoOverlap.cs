using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Two active bookings of the same SJA Hall room can no longer overlap, enforced by PostgreSQL itself: the app's
    /// check-then-insert let two simultaneous "Use now" (or bookings) both through. Half-open ranges, so back-to-back
    /// bookings (10:00-11:00, 11:00-12:00) are fine. If existing rows already overlap the constraint is skipped with a
    /// warning instead of failing the deploy (the app check still applies); re-run after fixing them.
    /// </summary>
    public partial class HallRoomNoOverlap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    CREATE EXTENSION IF NOT EXISTS btree_gist;
                    ALTER TABLE "Meetings" ADD CONSTRAINT "EX_Meetings_HallRoom_NoOverlap"
                        EXCLUDE USING gist ("Location" WITH =, tstzrange("StartAt", "EndAt", '[)') WITH &&)
                        WHERE ("Status" <> 'cancelled' AND "Location" LIKE 'SJA Hall%' AND "EndAt" > "StartAt");
                EXCEPTION WHEN OTHERS THEN
                    RAISE WARNING 'Hall room overlap constraint not added: %', SQLERRM;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE "Meetings" DROP CONSTRAINT IF EXISTS "EX_Meetings_HallRoom_NoOverlap";""");
        }
    }
}
