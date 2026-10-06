namespace IPop.Core.Entities;

/// Komentar pada tugas proyek (Diskusi Tugas, Fase 2). Balasan satu tingkat lewat
/// ParentCommentId — cukup untuk percakapan "komentar ▸ balasan" tanpa pohon dalam.
public class ProjectTaskComment
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid AuthorId { get; set; }
    public required string Content { get; set; }
    public Guid? ParentCommentId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }

    public ProjectTask Task { get; set; } = default!;
    public User Author { get; set; } = default!;
    public ProjectTaskComment? Parent { get; set; }
}

/// Riwayat aktivitas tugas: siapa mengubah apa dan kapan (Audit per tugas).
public class ProjectTaskActivity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid ActorId { get; set; }
    /// Aksi ringkas, mis. "status_changed", "comment_added", "attachment_added", "due_changed".
    public required string Action { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime CreatedAt { get; set; }

    public ProjectTask Task { get; set; } = default!;
    public User Actor { get; set; } = default!;
}

/// Lampiran berkas tugas dari penyimpanan NAS (referensi path, bukan salinan isi).
public class ProjectTaskAttachment
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid UploadedById { get; set; }
    public required string FileName { get; set; }
    public long FileSize { get; set; }
    /// Lokasi NAS: server + path FileStation (mis. "spj", "/Projects/foo/a.pdf").
    public required string SynologyServer { get; set; }
    public required string SynologyPath { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }

    public ProjectTask Task { get; set; } = default!;
    public User UploadedBy { get; set; } = default!;
}

/// Pengingat tenggat tugas: offset jam sebelum DueDate, dikirim lewat gerbang notifikasi.
public class ProjectTaskReminder
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    /// Jam sebelum tenggat (24 = sehari sebelum, 1 = satu jam sebelum).
    public int OffsetHours { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public ProjectTask Task { get; set; } = default!;
}

/// A file uploaded to a task through the shared attachment store (area "projects", NAS via the
/// service account), so every project member sees the same bytes. Kind separates field photos
/// and completion deliverables (the evidence flow) from plain task attachments.
public class ProjectTaskFile
{
    public const string KindField = "field";
    public const string KindDeliverable = "deliverable";
    public const string KindAttachment = "attachment";
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { KindField, KindDeliverable, KindAttachment };

    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid AttachmentId { get; set; }
    /// "field" | "deliverable" | "attachment"
    public required string Kind { get; set; }
    public Guid UploadedById { get; set; }
    public DateTime CreatedAt { get; set; }
    /// Soft delete: the link disappears from the task and the file stops being downloadable.
    public DateTime? DeletedAt { get; set; }

    public ProjectTask Task { get; set; } = default!;
    public Attachment Attachment { get; set; } = default!;
    public User UploadedBy { get; set; } = default!;
}
