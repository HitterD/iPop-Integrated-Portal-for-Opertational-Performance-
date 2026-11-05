using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <summary>Chat send replays ask whether a message already has a push intent: index PushJobs (Kind, RefId).</summary>
    public partial class PushJobKindRefIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PushJobs_Kind_RefId",
                table: "PushJobs",
                columns: new[] { "Kind", "RefId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PushJobs_Kind_RefId",
                table: "PushJobs");
        }
    }
}
