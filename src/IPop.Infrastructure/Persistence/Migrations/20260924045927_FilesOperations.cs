using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FilesOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SourceServer = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DestServer = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ItemsJson = table.Column<string>(type: "text", nullable: false),
                    DestDir = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TotalFiles = table.Column<int>(type: "integer", nullable: false),
                    CompletedFiles = table.Column<int>(type: "integer", nullable: false),
                    FailedFiles = table.Column<int>(type: "integer", nullable: false),
                    BytesTransferred = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileOperations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileOperations_Status",
                table: "FileOperations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_FileOperations_UserId_CreatedAt",
                table: "FileOperations",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileOperations");
        }
    }
}
