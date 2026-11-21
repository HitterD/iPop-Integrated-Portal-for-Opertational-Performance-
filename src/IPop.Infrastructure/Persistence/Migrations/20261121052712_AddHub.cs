using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHub : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HubTopics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Need = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HasReply = table.Column<bool>(type: "boolean", nullable: false),
                    SolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SolvedById = table.Column<Guid>(type: "uuid", nullable: true),
                    SolvedSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubTopics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HubTopics_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HubAsks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TopicId = table.Column<Guid>(type: "uuid", nullable: false),
                    PostId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AskedById = table.Column<Guid>(type: "uuid", nullable: false),
                    Question = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnsweredPostId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastRemindedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HeadToldAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubAsks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HubAsks_HubTopics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "HubTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HubAsks_Users_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HubAttachments",
                columns: table => new
                {
                    TopicId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    PostId = table.Column<Guid>(type: "uuid", nullable: true),
                    UploaderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubAttachments", x => new { x.TopicId, x.AttachmentId });
                    table.ForeignKey(
                        name: "FK_HubAttachments_HubTopics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "HubTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HubMembers",
                columns: table => new
                {
                    TopicId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedById = table.Column<Guid>(type: "uuid", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubMembers", x => new { x.TopicId, x.UserId });
                    table.ForeignKey(
                        name: "FK_HubMembers_HubTopics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "HubTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HubMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HubPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TopicId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubPosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HubPosts_HubTopics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "HubTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HubPosts_Users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HubAsks_TopicId",
                table: "HubAsks",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_HubAsks_ToUserId_AnsweredAt",
                table: "HubAsks",
                columns: new[] { "ToUserId", "AnsweredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HubAttachments_AttachmentId",
                table: "HubAttachments",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_HubMembers_UserId",
                table: "HubMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_HubPosts_AuthorId",
                table: "HubPosts",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_HubPosts_TopicId_CreatedAt",
                table: "HubPosts",
                columns: new[] { "TopicId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HubTopics_CreatedById",
                table: "HubTopics",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_HubTopics_LastActivityAt",
                table: "HubTopics",
                column: "LastActivityAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HubAsks");

            migrationBuilder.DropTable(
                name: "HubAttachments");

            migrationBuilder.DropTable(
                name: "HubMembers");

            migrationBuilder.DropTable(
                name: "HubPosts");

            migrationBuilder.DropTable(
                name: "HubTopics");
        }
    }
}
