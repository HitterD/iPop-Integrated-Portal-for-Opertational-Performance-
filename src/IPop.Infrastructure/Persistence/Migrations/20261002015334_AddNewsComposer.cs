using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsComposer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BodyFormat",
                table: "NewsPosts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "plain");

            migrationBuilder.AddColumn<int>(
                name: "ContentVersion",
                table: "NewsPosts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CoverMediaId",
                table: "NewsPosts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverMode",
                table: "NewsPosts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.CreateTable(
                name: "NewsComposerDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PostId = table.Column<Guid>(type: "uuid", nullable: true),
                    BaseContentVersion = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Body = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    BodyFormat = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequiresAcknowledgement = table.Column<bool>(type: "boolean", nullable: false),
                    AckDueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false),
                    PublishAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CoverMediaId = table.Column<Guid>(type: "uuid", nullable: true),
                    CoverMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PromotedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DiscardedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ScheduleAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextScheduleAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResetAcknowledgementsOnPromote = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsComposerDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NewsComposerDrafts_NewsPosts_PostId",
                        column: x => x.PostId,
                        principalTable: "NewsPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsComposerDrafts_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NewsComposerDraftAttachments",
                columns: table => new
                {
                    DraftId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsComposerDraftAttachments", x => new { x.DraftId, x.AttachmentId });
                    table.ForeignKey(
                        name: "FK_NewsComposerDraftAttachments_Attachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "Attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NewsComposerDraftAttachments_NewsComposerDrafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "NewsComposerDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NewsComposerMedia",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadDraftId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LocalPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NasPath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ThumbnailPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsComposerMedia", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NewsComposerMedia_Attachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "Attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_NewsComposerMedia_NewsComposerDrafts_UploadDraftId",
                        column: x => x.UploadDraftId,
                        principalTable: "NewsComposerDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_NewsComposerMedia_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NewsComposerDraftMedia",
                columns: table => new
                {
                    DraftId = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsComposerDraftMedia", x => new { x.DraftId, x.MediaId });
                    table.ForeignKey(
                        name: "FK_NewsComposerDraftMedia_NewsComposerDrafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "NewsComposerDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsComposerDraftMedia_NewsComposerMedia_MediaId",
                        column: x => x.MediaId,
                        principalTable: "NewsComposerMedia",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NewsPostMedia",
                columns: table => new
                {
                    NewsPostId = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsPostMedia", x => new { x.NewsPostId, x.MediaId });
                    table.ForeignKey(
                        name: "FK_NewsPostMedia_NewsComposerMedia_MediaId",
                        column: x => x.MediaId,
                        principalTable: "NewsComposerMedia",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsPostMedia_NewsPosts_NewsPostId",
                        column: x => x.NewsPostId,
                        principalTable: "NewsPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsComposerDraftAttachments_AttachmentId",
                table: "NewsComposerDraftAttachments",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsComposerDraftMedia_MediaId",
                table: "NewsComposerDraftMedia",
                column: "MediaId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsComposerDrafts_OwnerId_State_UpdatedAt",
                table: "NewsComposerDrafts",
                columns: new[] { "OwnerId", "State", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsComposerDrafts_PostId",
                table: "NewsComposerDrafts",
                column: "PostId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsComposerMedia_AttachmentId",
                table: "NewsComposerMedia",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsComposerMedia_OwnerId",
                table: "NewsComposerMedia",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsComposerMedia_OwnerId_Kind_DeletedAt",
                table: "NewsComposerMedia",
                columns: new[] { "OwnerId", "Kind", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsComposerMedia_UploadDraftId",
                table: "NewsComposerMedia",
                column: "UploadDraftId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsPostMedia_MediaId",
                table: "NewsPostMedia",
                column: "MediaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NewsComposerDraftAttachments");

            migrationBuilder.DropTable(
                name: "NewsComposerDraftMedia");

            migrationBuilder.DropTable(
                name: "NewsPostMedia");

            migrationBuilder.DropTable(
                name: "NewsComposerMedia");

            migrationBuilder.DropTable(
                name: "NewsComposerDrafts");

            migrationBuilder.DropColumn(
                name: "BodyFormat",
                table: "NewsPosts");

            migrationBuilder.DropColumn(
                name: "ContentVersion",
                table: "NewsPosts");

            migrationBuilder.DropColumn(
                name: "CoverMediaId",
                table: "NewsPosts");

            migrationBuilder.DropColumn(
                name: "CoverMode",
                table: "NewsPosts");
        }
    }
}
