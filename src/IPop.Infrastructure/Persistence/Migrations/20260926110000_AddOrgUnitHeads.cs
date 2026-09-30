using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrgUnitHeads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HeadUserId",
                table: "Divisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HeadUserId",
                table: "Departments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Divisions_HeadUserId",
                table: "Divisions",
                column: "HeadUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_HeadUserId",
                table: "Departments",
                column: "HeadUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Departments_Users_HeadUserId",
                table: "Departments",
                column: "HeadUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Divisions_Users_HeadUserId",
                table: "Divisions",
                column: "HeadUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Departments_Users_HeadUserId",
                table: "Departments");

            migrationBuilder.DropForeignKey(
                name: "FK_Divisions_Users_HeadUserId",
                table: "Divisions");

            migrationBuilder.DropIndex(
                name: "IX_Divisions_HeadUserId",
                table: "Divisions");

            migrationBuilder.DropIndex(
                name: "IX_Departments_HeadUserId",
                table: "Departments");

            migrationBuilder.DropColumn(
                name: "HeadUserId",
                table: "Divisions");

            migrationBuilder.DropColumn(
                name: "HeadUserId",
                table: "Departments");
        }
    }
}
