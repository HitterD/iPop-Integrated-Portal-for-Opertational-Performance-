namespace IPop.Application.Calls;

public interface ILiveKitTokenService
{
    string GenerateToken(string roomName, string identity, string name, TimeSpan? ttl = null);
    string? ServerUrl { get; }
    bool IsConfigured { get; }
}
