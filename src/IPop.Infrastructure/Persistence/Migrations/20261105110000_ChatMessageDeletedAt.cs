using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <summary>When a chat message was deleted (its files are purged from the NAS 30 days later).</summary>
    public partial class ChatMessageDeletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "ChatMessages",
                type: "timestamp with time zone",
                nullable: true);

            // Messages deleted before this column existed start their 30 days now.
            migrationBuilder.Sql("UPDATE \"ChatMessages\" SET \"DeletedAt\" = now() WHERE \"IsDeleted\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ChatMessages");
        }
    }
}
