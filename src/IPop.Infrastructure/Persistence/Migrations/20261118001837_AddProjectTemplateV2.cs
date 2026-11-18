using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectTemplateV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationDays",
                table: "ProjectTemplateTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceRule",
                table: "ProjectTemplateTasks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoleKey",
                table: "ProjectTemplateTasks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutomationsJson",
                table: "ProjectTemplates",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "ProjectTemplates",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationWeeks",
                table: "ProjectTemplates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OffsetsInWorkingDays",
                table: "ProjectTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RolesJson",
                table: "ProjectTemplates",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusesJson",
                table: "ProjectTemplates",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TabsJson",
                table: "ProjectTemplates",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Goal",
                table: "Projects",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationDays",
                table: "ProjectTemplateTasks");

            migrationBuilder.DropColumn(
                name: "RecurrenceRule",
                table: "ProjectTemplateTasks");

            migrationBuilder.DropColumn(
                name: "RoleKey",
                table: "ProjectTemplateTasks");

            migrationBuilder.DropColumn(
                name: "AutomationsJson",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "DurationWeeks",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "OffsetsInWorkingDays",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "RolesJson",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "StatusesJson",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "TabsJson",
                table: "ProjectTemplates");

            migrationBuilder.DropColumn(
                name: "Goal",
                table: "Projects");
        }
    }
}
