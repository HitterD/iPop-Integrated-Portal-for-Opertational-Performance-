using IPop.Application.News;
using IPop.Core.Entities;
using IPop.Infrastructure.News;
using IPop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// The schedule worker must never let a draft it cannot apply hold the head of the due queue: a
/// blocked draft (author deactivated, permission lost, live post moved on) is backed off, so
/// younger due drafts still get published.
public class NewsComposerScheduleWorkerTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<IPopDbContext> _options;
    private readonly ServiceProvider _services;

    public NewsComposerScheduleWorkerTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<IPopDbContext>().UseSqlite(_conn).Options;
        using (var db = new IPopDbContext(_options)) db.Database.EnsureCreated();

        _services = new ServiceCollection()
            .AddScoped(_ => new IPopDbContext(_options))
            .AddScoped<INewsService>(sp => new NewsService(sp.GetRequiredService<IPopDbContext>()))
            .AddScoped<INewsComposerService>(sp => new NewsComposerService(
                sp.GetRequiredService<IPopDbContext>(), sp.GetRequiredService<INewsService>()))
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _conn.Dispose();
    }

    private NewsComposerScheduleWorker Worker() => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<NewsComposerScheduleWorker>.Instance);

    private async Task<Guid> AddScheduledDraftAsync(Guid ownerId, string title, DateTime due, bool complete = true)
    {
        await using var db = new IPopDbContext(_options);
        var id = Guid.NewGuid();
        db.NewsComposerDrafts.Add(new NewsComposerDraft
        {
            Id = id, OwnerId = ownerId, State = "scheduled",
            Type = complete ? "news" : null,
            Title = complete ? title : null,
            Body = complete ? "<p>body</p>" : null,
            BodyFormat = "html", CoverMode = "auto",
            PublishAt = due, CreatedAt = due, UpdatedAt = due,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task AddOwnerAsync(Guid ownerId, bool active)
    {
        await using var db = new IPopDbContext(_options);
        db.Users.Add(new User
        {
            Id = ownerId, Nik = $"U{ownerId:N}"[..8], Name = "Owner", Email = "o@t.co",
            IsActive = active, MustChangePassword = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        // Role and permission are shared across owners in this fixture.
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == "HR");
        if (role is null)
        {
            role = new Role { Id = Guid.NewGuid(), Name = "HR" };
            db.Roles.Add(role);
        }
        var perm = await db.Permissions.FirstOrDefaultAsync(p => p.Code == "news.publish");
        if (perm is null)
        {
            perm = new Permission { Id = Guid.NewGuid(), Code = "news.publish", Description = "p" };
            db.Permissions.Add(perm);
        }
        db.UserRoles.Add(new UserRole { UserId = ownerId, RoleId = role.Id });
        if (!await db.RolePermissions.AnyAsync(rp => rp.RoleId == role.Id && rp.PermissionId == perm.Id))
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Worker_applies_a_due_scheduled_draft()
    {
        var owner = Guid.NewGuid();
        await AddOwnerAsync(owner, active: true);
        var draftId = await AddScheduledDraftAsync(owner, "Due now", DateTime.UtcNow.AddMinutes(-5));

        var applied = await Worker().RunDueAsync(default);

        Assert.Equal(1, applied);
        await using var db = new IPopDbContext(_options);
        Assert.Equal("promoted", (await db.NewsComposerDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId)).State);
        Assert.Equal("published", (await db.NewsPosts.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Worker_leaves_a_future_draft_alone()
    {
        var owner = Guid.NewGuid();
        await AddOwnerAsync(owner, active: true);
        var draftId = await AddScheduledDraftAsync(owner, "Later", DateTime.UtcNow.AddDays(1));

        Assert.Equal(0, await Worker().RunDueAsync(default));

        await using var db = new IPopDbContext(_options);
        Assert.Equal("scheduled", (await db.NewsComposerDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId)).State);
    }

    [Fact]
    public async Task A_blocked_draft_cannot_starve_later_due_drafts()
    {
        // Oldest-first ordering: fill a whole batch with blocked drafts (owner deactivated) that are
        // all due BEFORE the good draft, so a single pass cannot reach it. After the blocked drafts
        // are backed off, the next pass must skip them and publish the good one.
        var blockedOwner = Guid.NewGuid();
        await AddOwnerAsync(blockedOwner, active: false);
        var due = DateTime.UtcNow.AddHours(-3);
        for (var i = 0; i < 105; i++)
            await AddScheduledDraftAsync(blockedOwner, $"blocked {i}", due.AddSeconds(i));

        var goodOwner = Guid.NewGuid();
        await AddOwnerAsync(goodOwner, active: true);
        var goodId = await AddScheduledDraftAsync(goodOwner, "Good", due.AddHours(1));

        // Pass 1: the batch is full of older blocked drafts; the good one is not reached yet.
        await Worker().RunDueAsync(default);
        await using (var check = new IPopDbContext(_options))
        {
            Assert.Empty(await check.NewsPosts.AsNoTracking().ToListAsync());
            // The batch (100) that was attempted got backed off rather than retried in place at the
            // head of the queue; the remainder were simply never reached.
            var backedOff = await check.NewsComposerDrafts.AsNoTracking()
                .Where(d => d.OwnerId == blockedOwner && d.NextScheduleAttemptAt != null).CountAsync();
            Assert.Equal(100, backedOff);
        }

        // Pass 2: backed-off drafts are skipped, so the good draft is reached and published.
        Assert.Equal(1, await Worker().RunDueAsync(default));

        await using var db = new IPopDbContext(_options);
        Assert.Equal("promoted", (await db.NewsComposerDrafts.AsNoTracking().SingleAsync(d => d.Id == goodId)).State);
        var post = await db.NewsPosts.AsNoTracking().SingleAsync();
        Assert.Equal("Good", post.Title);
        // The blocked drafts are untouched and still recoverable by a human.
        Assert.Equal(105, await db.NewsComposerDrafts.AsNoTracking().CountAsync(d => d.State == "scheduled"));
    }

    [Fact]
    public async Task An_incomplete_scheduled_draft_is_blocked_and_backed_off_not_published()
    {
        var owner = Guid.NewGuid();
        await AddOwnerAsync(owner, active: true);
        // Simulates a draft that somehow froze without a title/body: the worker must refuse it, not
        // publish junk, and must not retry it in a tight loop.
        var draftId = await AddScheduledDraftAsync(owner, "x", DateTime.UtcNow.AddMinutes(-1), complete: false);

        Assert.Equal(0, await Worker().RunDueAsync(default));

        await using var db = new IPopDbContext(_options);
        var draft = await db.NewsComposerDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId);
        Assert.Equal("scheduled", draft.State);
        Assert.NotNull(draft.NextScheduleAttemptAt);
        Assert.Empty(await db.NewsPosts.AsNoTracking().ToListAsync());
    }
}