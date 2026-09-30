using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// Hand-written (no Designer): the target model lives in IPopDbContextModelSnapshot.
    [DbContext(typeof(IPopDbContext))]
    [Migration("20260930180000_AddUserPhotoLarge")]
    public partial class AddUserPhotoLarge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "LargeContent",
                table: "UserPhotos",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LargeContentType",
                table: "UserPhotos",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "LargeContent", table: "UserPhotos");
            migrationBuilder.DropColumn(name: "LargeContentType", table: "UserPhotos");
        }
    }
}
