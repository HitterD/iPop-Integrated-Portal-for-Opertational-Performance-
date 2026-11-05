using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <summary>Index AuditLogs (Action, EntityId) for per-record history lookups (approval history, undo).</summary>
    public partial class AuditLogActionEntityIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Action_EntityId",
                table: "AuditLogs",
                columns: new[] { "Action", "EntityId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_Action_EntityId",
                table: "AuditLogs");
        }
    }
}
