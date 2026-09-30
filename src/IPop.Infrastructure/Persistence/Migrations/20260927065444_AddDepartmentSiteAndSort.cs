using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDepartmentSiteAndSort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Divisions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SiteId",
                table: "Departments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Departments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Departments_SiteId",
                table: "Departments",
                column: "SiteId");

            migrationBuilder.AddForeignKey(
                name: "FK_Departments_Sites_SiteId",
                table: "Departments",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Departments_Sites_SiteId",
                table: "Departments");

            migrationBuilder.DropIndex(
                name: "IX_Departments_SiteId",
                table: "Departments");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Divisions");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "Departments");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Departments");
        }
    }
}
