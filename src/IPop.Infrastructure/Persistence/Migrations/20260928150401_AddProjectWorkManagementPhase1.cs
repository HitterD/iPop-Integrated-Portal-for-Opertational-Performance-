using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectWorkManagementPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "ProjectTasks",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "ProjectFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OptionsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectFields_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectGroups", x => x.Id);
                    table.UniqueConstraint("AK_ProjectGroups_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.ForeignKey(
                        name: "FK_ProjectGroups_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectTaskFieldValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectFieldId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTaskFieldValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectTaskFieldValues_ProjectFields_ProjectFieldId",
                        column: x => x.ProjectFieldId,
                        principalTable: "ProjectFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectTaskFieldValues_ProjectTasks_ProjectTaskId",
                        column: x => x.ProjectTaskId,
                        principalTable: "ProjectTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTasks_GroupId_ProjectId",
                table: "ProjectTasks",
                columns: new[] { "GroupId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTasks_ProjectId_GroupId",
                table: "ProjectTasks",
                columns: new[] { "ProjectId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectFields_ProjectId_SortOrder",
                table: "ProjectFields",
                columns: new[] { "ProjectId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectGroups_Id_ProjectId",
                table: "ProjectGroups",
                columns: new[] { "Id", "ProjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectGroups_ProjectId_SortOrder",
                table: "ProjectGroups",
                columns: new[] { "ProjectId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTaskFieldValues_ProjectFieldId",
                table: "ProjectTaskFieldValues",
                column: "ProjectFieldId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTaskFieldValues_ProjectTaskId_ProjectFieldId",
                table: "ProjectTaskFieldValues",
                columns: new[] { "ProjectTaskId", "ProjectFieldId" },
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO "ProjectGroups" ("Id", "ProjectId", "Name", "Color", "SortOrder")
                SELECT gen_random_uuid(), "Id", 'Tasks', '#8E8E93', 0 FROM "Projects";
                UPDATE "ProjectTasks" AS task SET "GroupId" = groups."Id"
                FROM "ProjectGroups" AS groups WHERE groups."ProjectId" = task."ProjectId";
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectTasks_ProjectGroups_GroupId_ProjectId",
                table: "ProjectTasks",
                columns: new[] { "GroupId", "ProjectId" },
                principalTable: "ProjectGroups",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectTasks_ProjectGroups_GroupId_ProjectId",
                table: "ProjectTasks");

            migrationBuilder.DropTable(
                name: "ProjectGroups");

            migrationBuilder.DropTable(
                name: "ProjectTaskFieldValues");

            migrationBuilder.DropTable(
                name: "ProjectFields");

            migrationBuilder.DropIndex(
                name: "IX_ProjectTasks_GroupId_ProjectId",
                table: "ProjectTasks");

            migrationBuilder.DropIndex(
                name: "IX_ProjectTasks_ProjectId_GroupId",
                table: "ProjectTasks");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "ProjectTasks");
        }
    }
}
