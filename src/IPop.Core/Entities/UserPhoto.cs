namespace IPop.Core.Entities;

/// A profile photo. The Id is random and changes on every upload, so it doubles as an
/// unguessable, immutable URL key: /api/photos/{id} can be cached forever and served to <img>.
public class UserPhoto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required byte[] Content { get; set; }
    public required string ContentType { get; set; }
    /// Optional up-to-1024px copy for the full-size viewer; the avatar keeps using Content.
    public byte[]? LargeContent { get; set; }
    public string? LargeContentType { get; set; }
    public DateTime CreatedAt { get; set; }
}
