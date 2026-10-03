namespace IPop.Application.Links;

/// GET /api/link-preview?url= — 200 body. Kind is "external" (OpenGraph/meta fetched by the server)
/// or "internal" (an iPop link; native data in <see cref="Internal"/>, never fetched over HTTP).
/// Image is an API-relative, signed proxy URL ("/api/link-preview/image?u=&exp=&sig=") or null.
public sealed record LinkPreviewDto(
    string Url,
    string Kind,
    string? Title,
    string? Description,
    string? SiteName,
    string? Image,
    InternalLinkDto? Internal);

/// Type: "news" | "project" | "task" | "page". For "page" (unknown route, or a target the viewer
/// may not see) only Path is set — Id/Title/Subtitle stay null so nothing leaks.
public sealed record InternalLinkDto(string Type, Guid? Id, string? Title, string? Subtitle, string Path);

public static class LinkPreviewKinds
{
    public const string External = "external";
    public const string Internal = "internal";
}
