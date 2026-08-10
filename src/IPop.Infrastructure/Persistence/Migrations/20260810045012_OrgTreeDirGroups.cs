using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrgTreeDirGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirGroups_DirGroups_ParentId",
                        column: x => x.ParentId,
                        principalTable: "DirGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrgNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrgNodes_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OrgNodes_OrgNodes_ParentId",
                        column: x => x.ParentId,
                        principalTable: "OrgNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserDirGroups",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DirGroupId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserDirGroups", x => new { x.UserId, x.DirGroupId });
                    table.ForeignKey(
                        name: "FK_UserDirGroups_DirGroups_DirGroupId",
                        column: x => x.DirGroupId,
                        principalTable: "DirGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserDirGroups_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserOrgNodes",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgNodeId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserOrgNodes", x => new { x.UserId, x.OrgNodeId });
                    table.ForeignKey(
                        name: "FK_UserOrgNodes_OrgNodes_OrgNodeId",
                        column: x => x.OrgNodeId,
                        principalTable: "OrgNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserOrgNodes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirGroups_ParentId",
                table: "DirGroups",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgNodes_DepartmentId",
                table: "OrgNodes",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgNodes_ParentId",
                table: "OrgNodes",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_UserDirGroups_DirGroupId",
                table: "UserDirGroups",
                column: "DirGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_UserOrgNodes_OrgNodeId",
                table: "UserOrgNodes",
                column: "OrgNodeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserDirGroups");

            migrationBuilder.DropTable(
                name: "UserOrgNodes");

            migrationBuilder.DropTable(
                name: "DirGroups");

            migrationBuilder.DropTable(
                name: "OrgNodes");
        }
    }
}
