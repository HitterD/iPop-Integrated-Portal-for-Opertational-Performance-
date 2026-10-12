using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace IPop.Infrastructure.MeetingRooms;

/// &lt;video src&gt; and &lt;a download&gt; cannot send the Bearer header, so a recording file is fetched with a
/// ticket scoped to one recording and one user: HMAC-SHA256 over "{recordingId:N}|{userId:N}|{expiresUnix}".
/// The key is HKDF-derived from an existing server secret with its own purpose label (never the secret itself),
/// so a leaked ticket (proxy log, history) opens only that recording, for that user, until it expires.
public sealed class RecordingTicketSigner
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
    private readonly byte[] _key;
    private readonly TimeProvider _time;

    public RecordingTicketSigner(string serverSecret, TimeProvider? time = null)
    {
        if (string.IsNullOrEmpty(serverSecret)) throw new ArgumentException("A server secret is required.", nameof(serverSecret));
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(serverSecret), 32,
            info: Encoding.UTF8.GetBytes("ipop.meeting-recording.ticket.v1"));
        _time = time ?? TimeProvider.System;
    }

    /// "{userId:N}.{expiresUnix}.{signature}" — URL-safe as is.
    public string Create(Guid recordingId, Guid userId)
    {
        var exp = _time.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        return $"{userId:N}.{exp.ToString(CultureInfo.InvariantCulture)}.{WebEncoders.Base64UrlEncode(Mac(recordingId, userId, exp))}";
    }

    /// The user the ticket was issued to, when it is genuine, unexpired and for this recording; otherwise null.
    public Guid? Verify(Guid recordingId, string? ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        var parts = ticket.Split('.');
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[0], "N", out var userId)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var exp)) return null;
        if (exp < _time.GetUtcNow().ToUnixTimeSeconds()) return null;
        byte[] given;
        try { given = WebEncoders.Base64UrlDecode(parts[2]); }
        catch (FormatException) { return null; }
        var expected = Mac(recordingId, userId, exp);
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected) ? userId : null;
    }

    private byte[] Mac(Guid recordingId, Guid userId, long exp) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{recordingId:N}|{userId:N}|{exp.ToString(CultureInfo.InvariantCulture)}"));
}
