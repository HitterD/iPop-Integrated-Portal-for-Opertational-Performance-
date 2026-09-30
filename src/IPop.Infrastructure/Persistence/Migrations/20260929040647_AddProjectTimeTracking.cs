using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectTimeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskTimeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Minutes = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AutoStopped = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTimeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskTimeEntries_ProjectTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ProjectTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskTimeEntries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskTimeEntries_TaskId",
                table: "TaskTimeEntries",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskTimeEntries_UserId_Running",
                table: "TaskTimeEntries",
                column: "UserId",
                unique: true,
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskTimeEntries_UserId_StartedAt",
                table: "TaskTimeEntries",
                columns: new[] { "UserId", "StartedAt" });

            // Actual hours used to be typed; keep each value as one "Data lama" entry so the
            // sum-of-entries total starts where it was. Credited to the first assignee (else the
            // creator), dated on the task's last update.
            migrationBuilder.Sql("""
                INSERT INTO "TaskTimeEntries" ("Id", "TaskId", "UserId", "StartedAt", "EndedAt", "Minutes", "Note", "Source", "AutoStopped", "CreatedAt")
                SELECT gen_random_uuid(), t."Id",
                       COALESCE((SELECT a."UserId" FROM "ProjectTaskAssignees" a WHERE a."TaskId" = t."Id" ORDER BY a."AssignedAt", a."UserId" LIMIT 1), t."CreatedById"),
                       t."UpdatedAt",
                       t."UpdatedAt" + make_interval(mins => ROUND(t."ActualHours" * 60)::int),
                       ROUND(t."ActualHours" * 60)::int, 'Data lama', 'legacy', FALSE, now()
                FROM "ProjectTasks" t
                WHERE t."ActualHours" > 0 AND ROUND(t."ActualHours" * 60) >= 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskTimeEntries");
        }
    }
}
