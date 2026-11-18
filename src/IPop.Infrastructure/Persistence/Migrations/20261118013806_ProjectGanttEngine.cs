using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectGanttEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConstraintDate",
                table: "ProjectTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConstraintType",
                table: "ProjectTasks",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeadlineDate",
                table: "ProjectTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LagDays",
                table: "ProjectTaskDependencies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LinkType",
                table: "ProjectTaskDependencies",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "FS");

            migrationBuilder.CreateTable(
                name: "ProjectBaselines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectBaselines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectBaselines_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectBaselines_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectBaselineTasks",
                columns: table => new
                {
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Start = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Due = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectBaselineTasks", x => new { x.BaselineId, x.TaskId });
                    table.ForeignKey(
                        name: "FK_ProjectBaselineTasks_ProjectBaselines_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "ProjectBaselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectBaselineTasks_ProjectTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ProjectTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBaselines_CreatedById",
                table: "ProjectBaselines",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBaselines_ProjectId",
                table: "ProjectBaselines",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBaselineTasks_TaskId",
                table: "ProjectBaselineTasks",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectBaselineTasks");

            migrationBuilder.DropTable(
                name: "ProjectBaselines");

            migrationBuilder.DropColumn(
                name: "ConstraintDate",
                table: "ProjectTasks");

            migrationBuilder.DropColumn(
                name: "ConstraintType",
                table: "ProjectTasks");

            migrationBuilder.DropColumn(
                name: "DeadlineDate",
                table: "ProjectTasks");

            migrationBuilder.DropColumn(
                name: "LagDays",
                table: "ProjectTaskDependencies");

            migrationBuilder.DropColumn(
                name: "LinkType",
                table: "ProjectTaskDependencies");
        }
    }
}
