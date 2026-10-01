using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrgPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HrisManagerNik",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HrisPosition",
                table: "Users",
                type: "text",
                nullable: true);

            // Keep what HRIS said: org chart Publish overwrites Position/ManagerNik from the chart.
            migrationBuilder.Sql("UPDATE \"Users\" SET \"HrisPosition\" = \"Position\", \"HrisManagerNik\" = \"ManagerNik\";");

            migrationBuilder.CreateTable(
                name: "OrgChartPublications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedByNik = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PositionCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgChartPublications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrgPositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgPositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrgPositions_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OrgPositions_OrgPositions_ParentId",
                        column: x => x.ParentId,
                        principalTable: "OrgPositions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrgPositions_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OrgPositionHolders",
                columns: table => new
                {
                    PositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgPositionHolders", x => new { x.PositionId, x.UserId });
                    table.ForeignKey(
                        name: "FK_OrgPositionHolders_OrgPositions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "OrgPositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrgPositionHolders_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrgPositionHolders_UserId",
                table: "OrgPositionHolders",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgPositions_DepartmentId",
                table: "OrgPositions",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgPositions_ParentId",
                table: "OrgPositions",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgPositions_SiteId",
                table: "OrgPositions",
                column: "SiteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrgChartPublications");

            migrationBuilder.DropTable(
                name: "OrgPositionHolders");

            migrationBuilder.DropTable(
                name: "OrgPositions");

            migrationBuilder.DropColumn(
                name: "HrisManagerNik",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "HrisPosition",
                table: "Users");
        }
    }
}
