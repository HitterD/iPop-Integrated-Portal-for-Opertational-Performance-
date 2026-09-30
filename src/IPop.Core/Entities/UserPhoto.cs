namespace IPop.Core.Entities;

/// A profile photo. The Id is random and changes on every upload, so it doubles as an
/// unguessable, immutable URL key: /api/photos/{id} can be cached forever and served to <img>.
public class UserPhoto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required byte[] Content { get; set; }
    public required string ContentType { get; set; }
    public DateTime CreatedAt { get; set; }
}
