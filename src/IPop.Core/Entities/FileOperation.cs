namespace IPop.Core.Entities;

/// <summary>
/// Long-running file operations (especially cross-server transfers) that survive page reloads.
/// Processed by FileOperationWorker outside the HTTP request lifecycle.
/// </summary>
public class FileOperation
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// "copy" | "move" | "delete"
    public required string Kind { get; set; }

    public required string SourceServer { get; set; }
    public required string DestServer { get; set; }

    /// JSON array of items: [{ "path": "...", "name": "...", "size": 123 }]
    public required string ItemsJson { get; set; }

    public string? DestDir { get; set; }

    /// "Queued" | "Running" | "Completed" | "CompletedWithErrors" | "Failed"
    public required string Status { get; set; }

    public int TotalFiles { get; set; }
    public int CompletedFiles { get; set; }
    public int FailedFiles { get; set; }
    public long BytesTransferred { get; set; }

    public string? Error { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = default!;
}
