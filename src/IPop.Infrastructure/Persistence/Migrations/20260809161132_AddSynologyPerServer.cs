using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSynologyPerServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SynologyCredentials",
                table: "SynologyCredentials");

            migrationBuilder.AddColumn<string>(
                name: "Server",
                table: "SynologyCredentials",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SynologyCredentials",
                table: "SynologyCredentials",
                columns: new[] { "UserId", "Server" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SynologyCredentials",
                table: "SynologyCredentials");

            migrationBuilder.DropColumn(
                name: "Server",
                table: "SynologyCredentials");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SynologyCredentials",
                table: "SynologyCredentials",
                column: "UserId");
        }
    }
}
