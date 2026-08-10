namespace IPop.Core.Entities;

public class SynologyCredential
{
    public Guid UserId { get; set; }
    public string Server { get; set; } = "spj";
    public required string Username { get; set; }
    public required string ProtectedPassword { get; set; }
    public bool IsInvalid { get; set; }
    public DateTime UpdatedAt { get; set; }
    public User User { get; set; } = default!;
}
