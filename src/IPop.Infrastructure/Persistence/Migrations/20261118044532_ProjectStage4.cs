using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectStage4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "Projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lessons",
                table: "Projects",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectAutomations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ConfigJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RunCount = table.Column<int>(type: "integer", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectAutomations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectAutomations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectForms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Intro = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AudienceScope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uuid", nullable: true),
                    IntakeGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    QuestionsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectForms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectForms_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectFormSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    FormId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmitterId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectFormSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectFormSubmissions_ProjectForms_FormId",
                        column: x => x.FormId,
                        principalTable: "ProjectForms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectFormSubmissions_ProjectTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ProjectTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectAutomations_ProjectId_Kind",
                table: "ProjectAutomations",
                columns: new[] { "ProjectId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectForms_ProjectId",
                table: "ProjectForms",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectFormSubmissions_FormId_SubmitterId_CreatedAt",
                table: "ProjectFormSubmissions",
                columns: new[] { "FormId", "SubmitterId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectFormSubmissions_TaskId",
                table: "ProjectFormSubmissions",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectAutomations");

            migrationBuilder.DropTable(
                name: "ProjectFormSubmissions");

            migrationBuilder.DropTable(
                name: "ProjectForms");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Lessons",
                table: "Projects");
        }
    }
}
