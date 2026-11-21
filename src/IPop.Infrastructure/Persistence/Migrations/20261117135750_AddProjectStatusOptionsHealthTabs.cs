using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectStatusOptionsHealthTabs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StatusOptionId",
                table: "ProjectTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Health",
                table: "Projects",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TabsJson",
                table: "Projects",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectStatusOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Color = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Stage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectStatusOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectStatusOptions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTasks_StatusOptionId",
                table: "ProjectTasks",
                column: "StatusOptionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectStatusOptions_ProjectId_SortOrder",
                table: "ProjectStatusOptions",
                columns: new[] { "ProjectId", "SortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectTasks_ProjectStatusOptions_StatusOptionId",
                table: "ProjectTasks",
                column: "StatusOptionId",
                principalTable: "ProjectStatusOptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectTasks_ProjectStatusOptions_StatusOptionId",
                table: "ProjectTasks");

            migrationBuilder.DropTable(
                name: "ProjectStatusOptions");

            migrationBuilder.DropIndex(
                name: "IX_ProjectTasks_StatusOptionId",
                table: "ProjectTasks");

            migrationBuilder.DropColumn(
                name: "StatusOptionId",
                table: "ProjectTasks");

            migrationBuilder.DropColumn(
                name: "Health",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "TabsJson",
                table: "Projects");
        }
    }
}
