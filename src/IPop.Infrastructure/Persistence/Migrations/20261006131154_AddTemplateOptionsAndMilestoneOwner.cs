using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateOptionsAndMilestoneOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssigneeNiks",
                table: "ProjectTemplateTasks",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "ProjectTemplateTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "ProjectTemplates",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "ProjectTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DepartmentId",
                table: "ProjectTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Icon",
                table: "ProjectTemplates",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBuiltIn",
                table: "ProjectTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UsedCount",
                table: "ProjectTemplates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "ProjectTemplates",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "everyone");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "ProjectMilestones",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectTemplateGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Color = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTemplateGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectTemplateGroups_ProjectTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "ProjectTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectTemplateMilestones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    DueOffsetDays = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    TaskIds = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTemplateMilestones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectTemplateMilestones_ProjectTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "ProjectTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplates_CreatedById",
                table: "ProjectTemplates",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplates_DepartmentId",
                table: "ProjectTemplates",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMilestones_OwnerId",
                table: "ProjectMilestones",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplateGroups_TemplateId_SortOrder",
                table: "ProjectTemplateGroups",
                columns: new[] { "TemplateId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplateMilestones_TemplateId_SortOrder",
                table: "ProjectTemplateMilestones",
                columns: new[] { "TemplateId", "SortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectMilestones_Users_OwnerId",
                table: "ProjectMilestones",
                column: "OwnerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectTemplates_Departments_DepartmentId",
                table: "ProjectTemplates",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectTemplates_Users_CreatedById",
                table: "ProjectTemplates",
                column: "CreatedById",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectMilestones_Users_OwnerId",
                table: "ProjectMilestones");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectTemplates_Departments_DepartmentId",
                table: "ProjectTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectTemplates_Users_CreatedById",
                table: "ProjectTemplates");

            migrationBuilder.DropTable(
                name: "ProjectTemplateGroups");

            migrationBuilder.DropTable(
                name: "ProjectTemplateMilestones");

            migrationBuilder.DropIndex(
                name: "IX_ProjectTemplates_CreatedById",
                table: "ProjectTemplates");

            migrationBuilder.DropIndex(
                name: "IX_ProjectTemplates_DepartmentId",
                table: "ProjectTemplates");

            migrationBuilder.DropIndex(
                name: "IX_ProjectMilestones_OwnerId",
                table: "ProjectMilestones");

            migrationBuilder.DropColumn(
                name: "AssigneeNiks",
                table: "ProjectTemplateTasks");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "ProjectTemplateTasks");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "Icon",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "IsBuiltIn",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "UsedCount",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "ProjectMilestones");
        }
    }
}
