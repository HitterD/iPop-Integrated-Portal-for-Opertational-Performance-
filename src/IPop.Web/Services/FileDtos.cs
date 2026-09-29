namespace IPop.Web.Services;

public record SynoItemDto(string Name, string Path, bool IsDir, long Size, long Mtime);
public record CredentialStatusDto(bool HasCredential, bool IsInvalid, string? Username, string? Server);
public record SiteConnectResultDto(string Server, string Status, string? Error);
