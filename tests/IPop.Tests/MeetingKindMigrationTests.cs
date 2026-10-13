using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 §1: the MeetingKind migration adds only Meetings.Kind and backfills it (review focus 1).
public class MeetingKindMigrationTests
{
    private static IReadOnlyList<MigrationOperation> UpOperations()
    {
        using var db = new IPopDbContext(new DbContextOptionsBuilder<IPopDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=not_used;Username=not_used;Password=not_used").Options);
        var migrations = db.GetService<IMigrationsAssembly>();
        var entry = Assert.Single(migrations.Migrations.Where(x => x.Key.EndsWith("_MeetingKind")));
        return migrations.CreateMigration(entry.Value, db.Database.ProviderName!).UpOperations;
    }

    [Fact]
    public void Adds_only_the_nullable_kind_column_then_backfill_sql()
    {
        var ops = UpOperations();
        Assert.Equal(4, ops.Count);
        var column = Assert.IsType<AddColumnOperation>(ops[0]);
        Assert.Equal(("Meetings", "Kind", "character varying(16)", true), (column.Table, column.Name, column.ColumnType, column.IsNullable));
        Assert.All(ops.Skip(1), op => Assert.IsType<SqlOperation>(op));
    }

    [Fact]
    public void Backfill_runs_room_then_voice_then_in_person_and_covers_cancelled_rows()
    {
        var sql = UpOperations().OfType<SqlOperation>().Select(o => o.Sql).ToArray();
        Assert.Equal(3, sql.Length);
        Assert.Contains("SET \"Kind\"='room'", sql[0]);
        Assert.Contains("ILIKE 'SJA Hall%'", sql[0]);
        Assert.Contains("SET \"Kind\"='voice'", sql[1]);
        Assert.Contains("\"Kind\" IS NULL", sql[1]);
        Assert.Contains("'Online · iPop Meeting'", sql[1]);
        Assert.Contains("EXISTS (SELECT 1 FROM \"MeetingAttendees\"", sql[1]);
        Assert.Contains("SET \"Kind\"='inperson'", sql[2]);
        Assert.Contains("\"Kind\" IS NULL", sql[2]);
        // No status filter: cancelled meetings get a kind too.
        Assert.All(sql, s => Assert.DoesNotContain("Status", s));
    }
}
