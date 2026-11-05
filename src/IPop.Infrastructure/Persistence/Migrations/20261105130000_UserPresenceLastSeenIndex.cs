using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <summary>Index Users.PresenceLastSeenAt for the Dashboard online list (it scanned every user).</summary>
    public partial class UserPresenceLastSeenIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Users_PresenceLastSeenAt",
                table: "Users",
                column: "PresenceLastSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_PresenceLastSeenAt",
                table: "Users");
        }
    }
}
