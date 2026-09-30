using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations
{
    /// Hand-written (no Designer): the target model lives in IPopDbContextModelSnapshot.
    /// "Add person" to calls: who rang a participant into a running call. Additive, nullable.
    [DbContext(typeof(IPopDbContext))]
    [Migration("20261001090000_AddCallParticipantInvitedBy")]
    public partial class AddCallParticipantInvitedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InvitedById",
                table: "CallParticipants",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "InvitedById", table: "CallParticipants");
        }
    }
}
