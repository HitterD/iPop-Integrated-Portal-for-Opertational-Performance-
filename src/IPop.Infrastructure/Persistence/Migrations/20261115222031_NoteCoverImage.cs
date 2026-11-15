using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NoteCoverImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CoverImageId",
                table: "NotePages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CoverPosY",
                table: "NotePages",
                type: "integer",
                nullable: false,
                defaultValue: 50);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverImageId",
                table: "NotePages");

            migrationBuilder.DropColumn(
                name: "CoverPosY",
                table: "NotePages");
        }
    }
}
