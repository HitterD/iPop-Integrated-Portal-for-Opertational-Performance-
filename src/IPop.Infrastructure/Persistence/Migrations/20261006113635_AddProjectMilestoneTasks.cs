using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectMilestoneTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectMilestoneTasks",
                columns: table => new
                {
                    MilestoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectMilestoneTasks", x => new { x.MilestoneId, x.TaskId });
                    table.ForeignKey(
                        name: "FK_ProjectMilestoneTasks_ProjectMilestones_MilestoneId",
                        column: x => x.MilestoneId,
                        principalTable: "ProjectMilestones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectMilestoneTasks_ProjectTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ProjectTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMilestoneTasks_TaskId",
                table: "ProjectMilestoneTasks",
                column: "TaskId");

            // Backfill: the old single-task link becomes the milestone's first linked task.
            migrationBuilder.Sql("""
                INSERT INTO "ProjectMilestoneTasks" ("MilestoneId", "TaskId")
                SELECT m."Id", m."TaskId" FROM "ProjectMilestones" AS m
                WHERE m."TaskId" IS NOT NULL
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectMilestoneTasks");
        }
    }
}
