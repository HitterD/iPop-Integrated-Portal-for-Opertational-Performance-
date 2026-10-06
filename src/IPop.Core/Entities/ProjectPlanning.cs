namespace IPop.Core.Entities;

/// Template proyek (Fase 4): kerangka tugas siap pakai untuk membuat proyek baru.
public class ProjectTemplate
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// Sumber: dibuat manual atau disalin dari proyek yang sudah ada.
    public Guid? SourceProjectId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public Project? SourceProject { get; set; }
    public ICollection<ProjectTemplateTask> Tasks { get; set; } = [];
}

/// Tugas kerangka dalam template proyek (bukan tugas nyata — dipakai saat membuat proyek).
public class ProjectTemplateTask
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public string? Priority { get; set; }
    /// Offset hari dari tanggal mulai proyek (untuk DueDate relatif).
    public int? DueOffsetDays { get; set; }
    public int SortOrder { get; set; }

    public ProjectTemplate Template { get; set; } = default!;
}

/// Milestone proyek (Fase 4): target besar beserta tanggalnya, bisa dikaitkan ke tugas.
public class ProjectMilestone
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Title { get; set; }
    public DateTime DueDate { get; set; }
    /// Tugas yang dikaitkan (opsional).
    public Guid? TaskId { get; set; }
    public DateTime? AchievedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Project Project { get; set; } = default!;
    public ProjectTask? Task { get; set; }
    /// Tasks that roll up into this milestone's progress (TaskId above is kept for old rows).
    public ICollection<ProjectMilestoneTask> LinkedTasks { get; set; } = [];
}

/// Many-to-many link: a milestone groups several tasks; it is achieved automatically when
/// every linked task is done (or when someone marks it).
public class ProjectMilestoneTask
{
    public Guid MilestoneId { get; set; }
    public Guid TaskId { get; set; }

    public ProjectMilestone Milestone { get; set; } = default!;
    public ProjectTask Task { get; set; } = default!;
}
