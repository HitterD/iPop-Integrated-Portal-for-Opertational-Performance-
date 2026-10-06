using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectTaskFilesAndCompletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAt",
                table: "ProjectTasks",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill: a done task completed when it last moved into "done"; tasks with no such
            // history row (created done, or older than the activity log) fall back to UpdatedAt.
            migrationBuilder.Sql("""
                UPDATE "ProjectTasks" AS t
                SET "CompletedAt" = COALESCE(
                    (SELECT MAX(a."CreatedAt") FROM "ProjectTaskActivities" AS a
                     WHERE a."TaskId" = t."Id" AND a."Action" = 'status_changed' AND a."NewValue" = 'done'),
                    t."UpdatedAt")
                WHERE t."Status" = 'done';
                """);

            migrationBuilder.CreateTable(
                name: "ProjectTaskFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UploadedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTaskFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectTaskFiles_Attachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "Attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectTaskFiles_ProjectTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ProjectTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectTaskFiles_Users_UploadedById",
                        column: x => x.UploadedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTaskFiles_AttachmentId",
                table: "ProjectTaskFiles",
                column: "AttachmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTaskFiles_TaskId_CreatedAt",
                table: "ProjectTaskFiles",
                columns: new[] { "TaskId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTaskFiles_UploadedById",
                table: "ProjectTaskFiles",
                column: "UploadedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectTaskFiles");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "ProjectTasks");
        }
    }
}
