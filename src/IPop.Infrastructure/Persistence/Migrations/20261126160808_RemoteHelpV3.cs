using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteHelpV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AskSeconds",
                table: "RemoteSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Alias",
                table: "RemoteMachines",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AllowedPermissions",
                table: "RemoteMachines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AlwaysAsk",
                table: "RemoteMachines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Excluded",
                table: "RemoteMachines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "RemoteMachines",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnattendedPermissions",
                table: "RemoteMachines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Monitors",
                table: "NotifierDevices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RemoteAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    What = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteAuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteAuditEntries_RemoteMachines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "RemoteMachines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteAuditEntries_At",
                table: "RemoteAuditEntries",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteAuditEntries_MachineId_At",
                table: "RemoteAuditEntries",
                columns: new[] { "MachineId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteAuditEntries");

            migrationBuilder.DropColumn(
                name: "AskSeconds",
                table: "RemoteSessions");

            migrationBuilder.DropColumn(
                name: "Alias",
                table: "RemoteMachines");

            migrationBuilder.DropColumn(
                name: "AllowedPermissions",
                table: "RemoteMachines");

            migrationBuilder.DropColumn(
                name: "AlwaysAsk",
                table: "RemoteMachines");

            migrationBuilder.DropColumn(
                name: "Excluded",
                table: "RemoteMachines");

            migrationBuilder.DropColumn(
                name: "Group",
                table: "RemoteMachines");

            migrationBuilder.DropColumn(
                name: "UnattendedPermissions",
                table: "RemoteMachines");

            migrationBuilder.DropColumn(
                name: "Monitors",
                table: "NotifierDevices");
        }
    }
}
