using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPop.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddChatDeliveryAndReadReceiptSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Existing read receipts were also used as delivery receipts. Preserve every row,
        // make delivery explicit, and leave ReadAt nullable for users who disable receipts.
        migrationBuilder.AddColumn<DateTime>(
            name: "DeliveredAt",
            table: "ChatReadReceipts",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AlterColumn<DateTime?>(
            name: "ReadAt",
            table: "ChatReadReceipts",
            type: "timestamp with time zone",
            nullable: true,
            oldClrType: typeof(DateTime),
            oldType: "timestamp with time zone");

        migrationBuilder.AddColumn<bool>(
            name: "ReadReceiptsEnabled",
            table: "ChatRoomUserSettings",
            type: "boolean",
            nullable: false,
            defaultValue: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ReadReceiptsEnabled",
            table: "ChatRoomUserSettings");

        migrationBuilder.DropColumn(
            name: "DeliveredAt",
            table: "ChatReadReceipts");

        migrationBuilder.AlterColumn<DateTime>(
            name: "ReadAt",
            table: "ChatReadReceipts",
            type: "timestamp with time zone",
            nullable: false,
            oldClrType: typeof(DateTime),
            oldType: "timestamp with time zone",
            oldNullable: true);
    }
}
