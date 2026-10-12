using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace IPop.Tests;

public class MeetingRoomsV2MigrationTests
{
    [Fact]
    public void Generated_migration_has_only_the_four_task_schema_changes()
    {
        using var db = new IPopDbContext(new DbContextOptionsBuilder<IPopDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=not_used;Username=not_used;Password=not_used").Options);
        var migrations = db.GetService<IMigrationsAssembly>();
        var entry = Assert.Single(migrations.Migrations.Where(x => x.Key.EndsWith("_MeetingRoomsV2")));
        var migration = migrations.CreateMigration(entry.Value, db.Database.ProviderName!);
        var operations = migration.UpOperations;
        Assert.Equal(7, operations.Count);
        var columns = operations.OfType<AddColumnOperation>().OrderBy(x => x.Table).ToArray();
        Assert.Equal(2, columns.Length);
        Assert.Equal(("MeetingRecordings", "MeetingId", "uuid", true), (columns[0].Table, columns[0].Name, columns[0].ColumnType, columns[0].IsNullable));
        Assert.Equal(("Meetings", "RoomPassword", "character varying(6)", true), (columns[1].Table, columns[1].Name, columns[1].ColumnType, columns[1].IsNullable));
        var tables = operations.OfType<CreateTableOperation>().OrderBy(x => x.Name).ToArray();
        Assert.Equal(new[] { "MeetingChatMessages", "MeetingRoomGuests" }, tables.Select(x => x.Name));
        Assert.Equal(new[] { "Id", "MeetingId", "RoomKey", "UserId", "Text", "Kind", "SentAt", "RecordingId", "OffsetSeconds" }.Order(), tables[0].Columns.Select(x => x.Name).Order());
        Assert.Empty(tables[0].ForeignKeys);
        Assert.Equal(new[] { "MeetingId", "UserId", "AdmittedAt" }.Order(), tables[1].Columns.Select(x => x.Name).Order());
        Assert.Equal(new[] { "MeetingId", "UserId" }, tables[1].PrimaryKey!.Columns);
        Assert.Equal(new[] { "Meetings", "Users" }, tables[1].ForeignKeys.Select(x => x.PrincipalTable).Order());
        Assert.All(tables[1].ForeignKeys, fk => Assert.Equal(ReferentialAction.Cascade, fk.OnDelete));
        var indexes = operations.OfType<CreateIndexOperation>().ToArray();
        Assert.Equal(3, indexes.Length);
        Assert.Contains(indexes, x => x.Table == "MeetingChatMessages" && x.Columns.SequenceEqual(new[] { "MeetingId", "SentAt" }));
        Assert.Contains(indexes, x => x.Table == "MeetingChatMessages" && x.Columns.SequenceEqual(new[] { "RecordingId" }));
        Assert.Contains(indexes, x => x.Table == "MeetingRoomGuests" && x.Columns.SequenceEqual(new[] { "UserId" }));
    }
}
