using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotePages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Emoji = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Cover = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Accent = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Font = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    FullWidth = table.Column<bool>(type: "boolean", nullable: false),
                    SmallText = table.Column<bool>(type: "boolean", nullable: false),
                    IsGuide = table.Column<bool>(type: "boolean", nullable: false),
                    SeedTemplate = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    YState = table.Column<byte[]>(type: "bytea", nullable: true),
                    StateVersion = table.Column<long>(type: "bigint", nullable: false),
                    PendingContributors = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Excerpt = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PlainText = table.Column<string>(type: "text", nullable: true),
                    TodoDone = table.Column<int>(type: "integer", nullable: false),
                    TodoTotal = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotePages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotePages_NotePages_ParentId",
                        column: x => x.ParentId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotePages_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NoteAttachments",
                columns: table => new
                {
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteAttachments", x => x.AttachmentId);
                    table.ForeignKey(
                        name: "FK_NoteAttachments_NotePages_PageId",
                        column: x => x.PageId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NoteFavorites",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteFavorites", x => new { x.UserId, x.PageId });
                    table.ForeignKey(
                        name: "FK_NoteFavorites_NotePages_PageId",
                        column: x => x.PageId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NoteFavorites_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NoteShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: true),
                    Level = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoteShares_ChatRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "ChatRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NoteShares_NotePages_PageId",
                        column: x => x.PageId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NoteShares_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NoteThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quote = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Anchor = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteThreads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoteThreads_NotePages_PageId",
                        column: x => x.PageId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NoteUpdates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoteUpdates_NotePages_PageId",
                        column: x => x.PageId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NoteVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<byte[]>(type: "bytea", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Contributors = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoteVersions_NotePages_PageId",
                        column: x => x.PageId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NoteVisits",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteVisits", x => new { x.UserId, x.PageId });
                    table.ForeignKey(
                        name: "FK_NoteVisits_NotePages_PageId",
                        column: x => x.PageId,
                        principalTable: "NotePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NoteVisits_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NoteComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoteComments_NoteThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "NoteThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NoteComments_Users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NoteAttachments_PageId",
                table: "NoteAttachments",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteComments_AuthorId",
                table: "NoteComments",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteComments_ThreadId_CreatedAt",
                table: "NoteComments",
                columns: new[] { "ThreadId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NoteFavorites_PageId",
                table: "NoteFavorites",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_NotePages_OwnerId_DeletedAt",
                table: "NotePages",
                columns: new[] { "OwnerId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotePages_ParentId",
                table: "NotePages",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteShares_PageId",
                table: "NoteShares",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteShares_RoomId",
                table: "NoteShares",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteShares_UserId",
                table: "NoteShares",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteThreads_PageId",
                table: "NoteThreads",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteUpdates_PageId",
                table: "NoteUpdates",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteVersions_PageId_CreatedAt",
                table: "NoteVersions",
                columns: new[] { "PageId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NoteVisits_PageId",
                table: "NoteVisits",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteVisits_UserId_VisitedAt",
                table: "NoteVisits",
                columns: new[] { "UserId", "VisitedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteAttachments");

            migrationBuilder.DropTable(
                name: "NoteComments");

            migrationBuilder.DropTable(
                name: "NoteFavorites");

            migrationBuilder.DropTable(
                name: "NoteShares");

            migrationBuilder.DropTable(
                name: "NoteUpdates");

            migrationBuilder.DropTable(
                name: "NoteVersions");

            migrationBuilder.DropTable(
                name: "NoteVisits");

            migrationBuilder.DropTable(
                name: "NoteThreads");

            migrationBuilder.DropTable(
                name: "NotePages");
        }
    }
}
