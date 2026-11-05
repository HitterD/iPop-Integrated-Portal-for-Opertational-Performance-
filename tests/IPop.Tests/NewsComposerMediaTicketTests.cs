using IPop.Api.Endpoints;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace IPop.Tests;

/// <summary>
/// Ticket mint/parse contract for composer media delivery. The endpoint re-checks access on every
/// request (see NewsComposerMediaServiceTests), so what matters here is that a ticket is bound to
/// exactly (media, user, context), is rejected when tampered with or replayed against another media,
/// and expires.
/// </summary>
public class NewsComposerMediaTicketTests
{
    private static readonly IDataProtectionProvider Dp = new EphemeralDataProtectionProvider();

    [Fact]
    public void Ticket_round_trips_the_exact_media_user_and_context()
    {
        var media = Guid.NewGuid();
        var user = Guid.NewGuid();
        var ctx = Guid.NewGuid();

        var token = NewsComposerMediaEndpoints.MintTicket(Dp, media, user, "draft", ctx);
        var read = NewsComposerMediaEndpoints.ReadTicket(Dp, token, media);

        Assert.NotNull(read);
        Assert.Equal(user, read!.Value.UserId);
        Assert.Equal("draft", read.Value.ContextKind);
        Assert.Equal(ctx, read.Value.ContextId);
    }

    [Fact]
    public void Ticket_is_rejected_for_a_different_media_or_a_tampered_value()
    {
        var media = Guid.NewGuid();
        var token = NewsComposerMediaEndpoints.MintTicket(Dp, media, Guid.NewGuid(), "post", Guid.NewGuid());

        // Replaying a valid ticket against another media id must not authorize that media.
        Assert.Null(NewsComposerMediaEndpoints.ReadTicket(Dp, token, Guid.NewGuid()));
        // Garbage / missing tickets are refused.
        Assert.Null(NewsComposerMediaEndpoints.ReadTicket(Dp, "not-a-ticket", media));
        Assert.Null(NewsComposerMediaEndpoints.ReadTicket(Dp, null, media));
        // A token minted by a different protector cannot be read.
        var other = new EphemeralDataProtectionProvider();
        Assert.Null(NewsComposerMediaEndpoints.ReadTicket(other, token, media));
    }

    [Fact]
    public void Ticket_expires()
    {
        // Mint through the real time-limited protector with a lifetime that is already over.
        var media = Guid.NewGuid();
        var expired = Dp.CreateProtector("IPop.NewsComposer.Media").ToTimeLimitedDataProtector()
            // Already expired when minted (an absolute time in the past): no sleep, so a fake/cached test clock
            // cannot make it look fresh.
            .Protect($"{media:N}|{Guid.NewGuid():N}|draft|{Guid.NewGuid():N}", DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Null(NewsComposerMediaEndpoints.ReadTicket(Dp, expired, media));
    }
}