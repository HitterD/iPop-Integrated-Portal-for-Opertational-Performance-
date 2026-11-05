using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace IPop.Tests;

/// Migrations written during the 2026-11 audit (hand-made: no EF tooling on the prod host). The model, the last
/// migration's target model and the snapshot must agree, or the next "dotnet ef migrations add" would re-emit them.
public class AuditMigrationTests
{
    private static IPopDbContext Db() => new(new DbContextOptionsBuilder<IPopDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=not_used;Username=not_used;Password=not_used").Options);

    [Fact]
    public void Model_has_no_changes_beyond_the_last_migration()
    {
        using var db = Db();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void PushJob_kind_ref_index_migration_adds_only_that_index()
    {
        using var db = Db();
        var migrations = db.GetService<IMigrationsAssembly>();
        var entry = Assert.Single(migrations.Migrations.Where(x => x.Key.EndsWith("_PushJobKindRefIndex")));
        var op = Assert.Single(migrations.CreateMigration(entry.Value, db.Database.ProviderName!).UpOperations);
        var index = Assert.IsType<CreateIndexOperation>(op);
        Assert.Equal("PushJobs", index.Table);
        Assert.Equal(["Kind", "RefId"], index.Columns);
    }

    [Fact]
    public void ChatMessage_deleted_at_migration_adds_the_column_and_backfills()
    {
        using var db = Db();
        var migrations = db.GetService<IMigrationsAssembly>();
        var entry = Assert.Single(migrations.Migrations.Where(x => x.Key.EndsWith("_ChatMessageDeletedAt")));
        var ops = migrations.CreateMigration(entry.Value, db.Database.ProviderName!).UpOperations;
        var col = Assert.Single(ops.OfType<AddColumnOperation>());
        Assert.Equal(("ChatMessages", "DeletedAt", true), (col.Table, col.Name, col.IsNullable));
        Assert.Single(ops.OfType<SqlOperation>());
    }
}
