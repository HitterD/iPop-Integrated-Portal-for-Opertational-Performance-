using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifierDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotifierDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MachineName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WindowsUser = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OsVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LocalIps = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RemoteIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotifierDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotifierDevices_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotifierDevices_LastSeenAt",
                table: "NotifierDevices",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotifierDevices_UserId_DeviceKey",
                table: "NotifierDevices",
                columns: new[] { "UserId", "DeviceKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotifierDevices");
        }
    }
}
