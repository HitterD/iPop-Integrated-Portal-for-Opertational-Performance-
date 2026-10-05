using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdeskTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketRefCounters",
                columns: table => new
                {
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Last = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketRefCounters", x => x.Year);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Origin = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ExternalRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IdeskTicketId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TicketNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Module = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModuleName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    Device = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Software = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CriticalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    HardwareType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ScheduledDate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    ScheduledTime = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    AssigneeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SlaTarget = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefreshedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SendAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PendingPayload = table.Column<string>(type: "text", nullable: true),
                    AttachmentsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tickets_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketUserStates",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Offline = table.Column<bool>(type: "boolean", nullable: false),
                    NotRegistered = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketUserStates", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_TicketUserStates_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Assignee = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WebhookEventId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketEvents_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketEvents_TicketId_OccurredAt",
                table: "TicketEvents",
                columns: new[] { "TicketId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketEvents_WebhookEventId",
                table: "TicketEvents",
                column: "WebhookEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ExternalRef",
                table: "Tickets",
                column: "ExternalRef",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_IdeskTicketId",
                table: "Tickets",
                column: "IdeskTicketId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Status_NextAttemptAt",
                table: "Tickets",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_UserId_RemovedAt",
                table: "Tickets",
                columns: new[] { "UserId", "RemovedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketEvents");

            migrationBuilder.DropTable(
                name: "TicketRefCounters");

            migrationBuilder.DropTable(
                name: "TicketUserStates");

            migrationBuilder.DropTable(
                name: "Tickets");
        }
    }
}
