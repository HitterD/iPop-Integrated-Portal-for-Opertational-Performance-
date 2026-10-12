using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MeetingRoomOwnershipLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MeetingRoomOwnerships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoomKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    WindowStartAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingRoomOwnerships", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingRoomOwnerships_RoomKey_MeetingId",
                table: "MeetingRoomOwnerships",
                columns: new[] { "RoomKey", "MeetingId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeetingRoomOwnerships");
        }
    }
}
