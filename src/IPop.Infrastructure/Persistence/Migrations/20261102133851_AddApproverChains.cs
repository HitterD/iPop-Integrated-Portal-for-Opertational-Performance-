using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApproverChains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApproverChains",
                columns: table => new
                {
                    Nik = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Slot1Nik = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Slot2Nik = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Slot3Nik = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Slot4Nik = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Slot5Nik = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Slot6Nik = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Slot7Nik = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Slot8Nik = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Slot9Nik = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SourceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SourceJobTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SourceLevel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourceWorkLocation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByNik = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApproverChains", x => x.Nik);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApproverChains_Slot1Nik",
                table: "ApproverChains",
                column: "Slot1Nik");

            migrationBuilder.CreateIndex(
                name: "IX_ApproverChains_Slot2Nik",
                table: "ApproverChains",
                column: "Slot2Nik");

            migrationBuilder.CreateIndex(
                name: "IX_ApproverChains_Slot3Nik",
                table: "ApproverChains",
                column: "Slot3Nik");

            migrationBuilder.CreateIndex(
                name: "IX_ApproverChains_Slot4Nik",
                table: "ApproverChains",
                column: "Slot4Nik");

            migrationBuilder.CreateIndex(
                name: "IX_ApproverChains_Slot5Nik",
                table: "ApproverChains",
                column: "Slot5Nik");

            migrationBuilder.CreateIndex(
                name: "IX_ApproverChains_Slot6Nik",
                table: "ApproverChains",
                column: "Slot6Nik");

            migrationBuilder.CreateIndex(
                name: "IX_ApproverChains_Slot7Nik",
                table: "ApproverChains",
                column: "Slot7Nik");

            migrationBuilder.CreateIndex(
                name: "IX_ApproverChains_Slot8Nik",
                table: "ApproverChains",
                column: "Slot8Nik");

            migrationBuilder.CreateIndex(
                name: "IX_ApproverChains_Slot9Nik",
                table: "ApproverChains",
                column: "Slot9Nik");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApproverChains");
        }
    }
}
