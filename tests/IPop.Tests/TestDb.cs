using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IPop.Tests;

/// SQLite in-memory — a real relational engine, so ExecuteUpdateAsync works
/// (the InMemory provider would throw on it). The connection must stay open
/// for the database to live.
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<IPopDbContext> _options;
    public IPopDbContext Db { get; }

    public TestDb()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<IPopDbContext>().UseSqlite(_conn).Options;
        Db = new IPopDbContext(_options);
        Db.Database.EnsureCreated();   // applies the HasData seed for HrisSyncState (Id=1)
    }

    /// ExecuteUpdateAsync writes behind the change tracker's back, so any
    /// read after one must use a fresh context or it sees stale tracked state.
    /// Production gets this for free via one scoped context per request.
    public IPopDbContext Verify() => new(_options);

    public User AddUser(string nik, bool active = true, string? passwordHash = null, bool mustChange = true)
    {
        var u = new User
        {
            Id = Guid.NewGuid(), Nik = nik, Name = $"User {nik}", Email = $"{nik}@t.co",
            PasswordHash = passwordHash, MustChangePassword = mustChange, IsActive = active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        Db.Users.Add(u);
        Db.SaveChanges();
        return u;
    }

    public void Dispose() { Db.Dispose(); _conn.Dispose(); }
}
