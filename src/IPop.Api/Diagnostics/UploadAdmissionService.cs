using System.Security.Claims;
using System.Security.Cryptography;

namespace IPop.Api.Diagnostics;

/// One process / one API replica only. Reservations and legacy transfers share the same budget.
/// No queue, no replay tombstones: only live reservations (<= global cap) and active owners are stored.
public sealed class UploadAdmissionService(IConfiguration configuration, TimeProvider clock)
{
    public const string Header = "X-Upload-Reservation";
    public static readonly TimeSpan ReservationLifetime = TimeSpan.FromSeconds(30);
    private readonly int maxConcurrent = Math.Clamp(configuration.GetValue("Uploads:MaxConcurrent", 4), 1, 64);
    private readonly int maxPerUser = Math.Clamp(configuration.GetValue("Uploads:MaxConcurrentPerUser", 2), 1,
        Math.Clamp(configuration.GetValue("Uploads:MaxConcurrent", 4), 1, 64));
    private readonly object gate = new();
    private readonly Dictionary<string, int> users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reservation> reservations = new(StringComparer.Ordinal);
    private int occupied;
    private sealed record Reservation(string Owner, string Route, DateTimeOffset Expires);

    public static string? Owner(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
        ? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? user.FindFirstValue("nik")
        : null;

    public static bool IsUpload(PathString path) =>
        path.Equals("/api/mail/attachments/upload", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/chat/attachments/upload", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/files/upload", StringComparison.OrdinalIgnoreCase);

    public string? Reserve(string owner, string route)
    {
        if (string.IsNullOrEmpty(owner) || !IsUpload(route)) return null;
        lock (gate)
        {
            Sweep();
            if (!Acquire(owner)) return null;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            reservations.Add(token, new(owner, route, clock.GetUtcNow() + ReservationLifetime));
            return token;
        }
    }

    // Non-null token never falls back to legacy admission, including unknown tokens after restart.
    public IDisposable? TryStart(string owner, string route, string? token)
    {
        if (string.IsNullOrEmpty(owner) || !IsUpload(route)) return null;
        lock (gate)
        {
            Sweep();
            if (token is not null)
            {
                if (token.Length != 64 || !reservations.TryGetValue(token, out var reservation)
                    || !Matches(reservation, owner, route)) return null;
                reservations.Remove(token); // atomic transfer: still occupied, active has no expiry
            }
            else if (!Acquire(owner)) return null;
            return new Permit(this, owner);
        }
    }

    public bool Cancel(string owner, string route, string token)
    {
        lock (gate)
        {
            Sweep();
            if (token.Length != 64 || !reservations.TryGetValue(token, out var reservation)
                || !Matches(reservation, owner, route)) return false;
            reservations.Remove(token);
            Release(owner);
            return true;
        }
    }

    private static bool Matches(Reservation r, string owner, string route) => r.Owner == owner
        && string.Equals(r.Route, route, StringComparison.OrdinalIgnoreCase);
    private bool Acquire(string owner)
    {
        var count = users.GetValueOrDefault(owner);
        if (occupied >= maxConcurrent || count >= maxPerUser) return false;
        occupied++;
        users[owner] = count + 1;
        return true;
    }
    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (token, reservation) in reservations.ToArray())
            if (reservation.Expires <= now)
            {
                reservations.Remove(token);
                Release(reservation.Owner);
            }
    }
    private void Release(string owner)
    {
        lock (gate)
        {
            occupied--;
            var count = users[owner] - 1;
            if (count == 0) users.Remove(owner); else users[owner] = count;
        }
    }
    private sealed class Permit(UploadAdmissionService service, string owner) : IDisposable
    {
        private UploadAdmissionService? admission = service;
        public void Dispose() => Interlocked.Exchange(ref admission, null)?.Release(owner);
    }
}
