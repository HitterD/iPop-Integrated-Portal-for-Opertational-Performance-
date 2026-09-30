using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCallRoomChatRoomId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ChatRoomId",
                table: "CallRooms",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CallRooms_ChatRoomId_Status",
                table: "CallRooms",
                columns: new[] { "ChatRoomId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_CallRooms_ChatRooms_ChatRoomId",
                table: "CallRooms",
                column: "ChatRoomId",
                principalTable: "ChatRooms",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CallRooms_ChatRooms_ChatRoomId",
                table: "CallRooms");

            migrationBuilder.DropIndex(
                name: "IX_CallRooms_ChatRoomId_Status",
                table: "CallRooms");

            migrationBuilder.DropColumn(
                name: "ChatRoomId",
                table: "CallRooms");
        }
    }
}
