using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteMachines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Permissions",
                table: "RemoteSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MachineId",
                table: "NotifierDevices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RemoteMachines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineKey = table.Column<string>(type: "character varying(110)", maxLength: 110, nullable: false),
                    ComputerName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConnectId = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    Unattended = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PasswordUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SetById = table.Column<Guid>(type: "uuid", nullable: true),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteMachines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotifierDevices_MachineId",
                table: "NotifierDevices",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteMachines_ConnectId",
                table: "RemoteMachines",
                column: "ConnectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteMachines_MachineKey",
                table: "RemoteMachines",
                column: "MachineKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_NotifierDevices_RemoteMachines_MachineId",
                table: "NotifierDevices",
                column: "MachineId",
                principalTable: "RemoteMachines",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NotifierDevices_RemoteMachines_MachineId",
                table: "NotifierDevices");

            migrationBuilder.DropTable(
                name: "RemoteMachines");

            migrationBuilder.DropIndex(
                name: "IX_NotifierDevices_MachineId",
                table: "NotifierDevices");

            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "RemoteSessions");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "NotifierDevices");
        }
    }
}
