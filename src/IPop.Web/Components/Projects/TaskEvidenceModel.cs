using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace IPop.Web.Components.Projects;

public class TaskEvidenceItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string? DataUrl { get; set; }
    public long Size { get; set; }
    public string Type { get; set; } = "photo"; // "photo" | "file"
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

public class TaskProgressStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = "";
    public bool IsDone { get; set; }
}

public class TaskExtensionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime? OriginalDueDate { get; set; }
    public DateTime NewDueDate { get; set; }
    public string Reason { get; set; } = "";
    public string? AttachmentName { get; set; }
    public string? AttachmentDataUrl { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
}

public class TaskEvidenceData
{
    public List<TaskProgressStep> ProgressSteps { get; set; } = [];
    public List<TaskEvidenceItem> FieldPhotos { get; set; } = [];
    public string? CompletionNotes { get; set; }
    public List<TaskEvidenceItem> CompletionDeliverables { get; set; } = [];
    public List<TaskExtensionRecord> ExtensionHistory { get; set; } = [];

    public bool IsExtended => ExtensionHistory.Count > 0;
    public TaskExtensionRecord? LatestExtension => ExtensionHistory.LastOrDefault();
}

public static class TaskEvidenceHelper
{
    private const string TagStart = "[EVIDENCE_DATA]";
    private const string TagEnd = "[/EVIDENCE_DATA]";

    public static (string CleanDescription, TaskEvidenceData Evidence) Parse(string? rawDescription)
    {
        if (string.IsNullOrWhiteSpace(rawDescription))
        {
            return ("", new TaskEvidenceData());
        }

        var startIdx = rawDescription.IndexOf(TagStart, StringComparison.OrdinalIgnoreCase);
        var endIdx = rawDescription.IndexOf(TagEnd, StringComparison.OrdinalIgnoreCase);

        if (startIdx >= 0 && endIdx > startIdx)
        {
            var clean = rawDescription[..startIdx].TrimEnd('-', ' ', '\r', '\n');
            var jsonStart = startIdx + TagStart.Length;
            var json = rawDescription[jsonStart..endIdx].Trim();

            try
            {
                var parsed = JsonSerializer.Deserialize<TaskEvidenceData>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return (clean, parsed ?? new TaskEvidenceData());
            }
            catch
            {
                return (clean, new TaskEvidenceData());
            }
        }

        return (rawDescription, new TaskEvidenceData());
    }

    public static string Serialize(string? cleanDescription, TaskEvidenceData evidence)
    {
        var clean = cleanDescription?.Trim() ?? "";

        // Strip heavy base64 dataUrl from the serialized string to guarantee staying within 4000 char DB limit
        var safeCopy = new TaskEvidenceData
        {
            CompletionNotes = evidence.CompletionNotes,
            ProgressSteps = evidence.ProgressSteps,
            FieldPhotos = evidence.FieldPhotos.ConvertAll(p => new TaskEvidenceItem
            {
                Id = p.Id,
                Name = p.Name,
                Size = p.Size,
                Type = p.Type,
                UploadedAt = p.UploadedAt,
                DataUrl = null // stored in localStorage
            }),
            CompletionDeliverables = evidence.CompletionDeliverables.ConvertAll(d => new TaskEvidenceItem
            {
                Id = d.Id,
                Name = d.Name,
                Size = d.Size,
                Type = d.Type,
                UploadedAt = d.UploadedAt,
                DataUrl = null // stored in localStorage
            }),
            ExtensionHistory = evidence.ExtensionHistory.ConvertAll(e => new TaskExtensionRecord
            {
                Id = e.Id,
                OriginalDueDate = e.OriginalDueDate,
                NewDueDate = e.NewDueDate,
                Reason = e.Reason,
                AttachmentName = e.AttachmentName,
                AttachmentDataUrl = null, // stored in localStorage
                RequestedAt = e.RequestedAt
            })
        };

        var json = JsonSerializer.Serialize(safeCopy);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return $"{TagStart}\n{json}\n{TagEnd}";
        }

        return $"{clean}\n\n---\n{TagStart}\n{json}\n{TagEnd}";
    }
}
