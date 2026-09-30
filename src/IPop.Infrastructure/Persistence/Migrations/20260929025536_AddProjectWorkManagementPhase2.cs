using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectWorkManagementPhase2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeleteBatchId",
                table: "ProjectTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "ProjectTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectSavedViews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsShared = table.Column<bool>(type: "boolean", nullable: false),
                    StateJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectSavedViews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectSavedViews_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectSavedViews_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectTaskDependencies",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    DependsOnTaskId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTaskDependencies", x => new { x.TaskId, x.DependsOnTaskId });
                    table.ForeignKey(
                        name: "FK_ProjectTaskDependencies_ProjectTasks_DependsOnTaskId",
                        column: x => x.DependsOnTaskId,
                        principalTable: "ProjectTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectTaskDependencies_ProjectTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ProjectTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTasks_DeleteBatchId",
                table: "ProjectTasks",
                column: "DeleteBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSavedViews_OwnerId",
                table: "ProjectSavedViews",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSavedViews_ProjectId_OwnerId",
                table: "ProjectSavedViews",
                columns: new[] { "ProjectId", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTaskDependencies_DependsOnTaskId",
                table: "ProjectTaskDependencies",
                column: "DependsOnTaskId");

            // Backfill rows from the legacy comma-separated "Dependencies" string: only
            // well-formed GUIDs that point at another task in the same project.
            migrationBuilder.Sql("""
                INSERT INTO "ProjectTaskDependencies" ("TaskId", "DependsOnTaskId")
                SELECT DISTINCT t."Id", d."Id"
                FROM "ProjectTasks" t
                CROSS JOIN LATERAL unnest(string_to_array(t."Dependencies", ',')) AS raw(v)
                JOIN "ProjectTasks" d
                  ON btrim(raw.v) ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                 AND d."Id" = btrim(raw.v)::uuid
                 AND d."ProjectId" = t."ProjectId"
                 AND d."Id" <> t."Id"
                WHERE t."Dependencies" IS NOT NULL AND btrim(t."Dependencies") <> ''
                ON CONFLICT DO NOTHING;
                """);

            // Live-task lookups per project (the soft-delete query filter adds DeletedAt IS NULL everywhere).
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_ProjectTasks_ProjectId_Live"
                ON "ProjectTasks" ("ProjectId") WHERE "DeletedAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_ProjectTasks_ProjectId_Live";""");

            migrationBuilder.DropTable(
                name: "ProjectSavedViews");

            migrationBuilder.DropTable(
                name: "ProjectTaskDependencies");

            migrationBuilder.DropIndex(
                name: "IX_ProjectTasks_DeleteBatchId",
                table: "ProjectTasks");

            migrationBuilder.DropColumn(
                name: "DeleteBatchId",
                table: "ProjectTasks");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ProjectTasks");
        }
    }
}
