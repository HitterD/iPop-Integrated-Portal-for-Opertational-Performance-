using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// Hand-written (no Designer): the target model lives in IPopDbContextModelSnapshot.
    [DbContext(typeof(IPopDbContext))]
    [Migration("20260930120000_AddNewsVideos")]
    public partial class AddNewsVideos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NewsVideos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NewsPostId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    LocalPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    NasPath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ThumbnailPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PopupUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UploaderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsVideos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NewsVideos_NewsPosts_NewsPostId",
                        column: x => x.NewsPostId,
                        principalTable: "NewsPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NewsVideoViews",
                columns: table => new
                {
                    NewsVideoId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PopupSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PlayedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DownloadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsVideoViews", x => new { x.NewsVideoId, x.UserId });
                    table.ForeignKey(
                        name: "FK_NewsVideoViews_NewsVideos_NewsVideoId",
                        column: x => x.NewsVideoId,
                        principalTable: "NewsVideos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsVideoViews_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsVideos_NewsPostId",
                table: "NewsVideos",
                column: "NewsPostId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsVideos_PopupUntil",
                table: "NewsVideos",
                column: "PopupUntil");

            migrationBuilder.CreateIndex(
                name: "IX_NewsVideoViews_UserId",
                table: "NewsVideoViews",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "NewsVideoViews");
            migrationBuilder.DropTable(name: "NewsVideos");
        }
    }
}
