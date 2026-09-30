using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// Hand-written (no Designer): the target model lives in IPopDbContextModelSnapshot.
    [DbContext(typeof(IPopDbContext))]
    [Migration("20260930150000_AddCallHistorySeenAt")]
    public partial class AddCallHistorySeenAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SeenAt",
                table: "CallHistories",
                type: "timestamp with time zone",
                nullable: true);

            // Missed calls from before this feature had no way to be cleared: start everyone at zero.
            migrationBuilder.Sql("UPDATE \"CallHistories\" SET \"SeenAt\" = \"EndedAt\" WHERE \"Result\" = 'missed';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeenAt",
                table: "CallHistories");
        }
    }
}
