using IPop.Api.Endpoints;
using IPop.Application.Realtime;
using Xunit;

namespace IPop.Tests;

public class BadgeEndpointTests
{
    private sealed class FixedBadge(string key, int count) : IBadgeProvider
    {
        public string Key => key;
        public Guid? SeenUser { get; private set; }
        public Task<int> CountAsync(Guid userId, CancellationToken ct) { SeenUser = userId; return Task.FromResult(count); }
    }

    [Fact]
    public async Task NoProviders_ReturnsEmpty()
    {
        Assert.Empty(await MeEndpoints.BadgesCore([], Guid.NewGuid(), default));
    }

    [Fact]
    public async Task AggregatesProviders_ForCaller()
    {
        var user = Guid.NewGuid();
        var chat = new FixedBadge("chat", 3);
        var result = await MeEndpoints.BadgesCore([chat, new FixedBadge("mail", 0)], user, default);

        Assert.Equal(new Dictionary<string, int> { ["chat"] = 3, ["mail"] = 0 }, result);
        Assert.Equal(user, chat.SeenUser);
    }
}
