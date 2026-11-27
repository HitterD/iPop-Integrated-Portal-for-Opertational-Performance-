using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatAllowMemberMentionAll : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent, and existing groups keep today's behaviour for owners/admins while members
            // get @all ON by default (owner decision); admins can switch it off per group.
            migrationBuilder.Sql("""ALTER TABLE "ChatRooms" ADD COLUMN IF NOT EXISTS "AllowMemberMentionAll" boolean NOT NULL DEFAULT TRUE;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE "ChatRooms" DROP COLUMN IF EXISTS "AllowMemberMentionAll";""");
        }
    }
}
