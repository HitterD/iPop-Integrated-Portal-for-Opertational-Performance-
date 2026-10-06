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

    /// Who saved it (null for built-ins and rows from before template ownership).
    public Guid? CreatedById { get; set; }
    /// "private" (creator only) | "department" (DepartmentId) | "everyone".
    public string Visibility { get; set; } = ProjectTemplateVisibility.Everyone;
    /// The department that may use it when Visibility = "department".
    public Guid? DepartmentId { get; set; }
    /// Times a project was created from it.
    public int UsedCount { get; set; }
    /// Optional Lucide icon name and tone key ("acc" | "inf" | "war" | "vio" | "dan").
    public string? Icon { get; set; }
    public string? Color { get; set; }
    /// Shipped with the app (seeded by fixed id); never deletable.
    public bool IsBuiltIn { get; set; }

    public Project? SourceProject { get; set; }
    public User? CreatedBy { get; set; }
    public Department? Department { get; set; }
    public ICollection<ProjectTemplateTask> Tasks { get; set; } = [];
    public ICollection<ProjectTemplateGroup> Groups { get; set; } = [];
    public ICollection<ProjectTemplateMilestone> Milestones { get; set; } = [];
}

public static class ProjectTemplateVisibility
{
    public const string Private = "private";
    public const string Department = "department";
    public const string Everyone = "everyone";
    public static readonly string[] All = [Private, Department, Everyone];
}

/// A section (ProjectGroup) recreated when a project is made from the template.
public class ProjectTemplateGroup
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public required string Name { get; set; }
    public string Color { get; set; } = "#8E8E93";
    public int SortOrder { get; set; }

    public ProjectTemplate Template { get; set; } = default!;
}

/// A milestone recreated with DueDate = start date + DueOffsetDays.
public class ProjectTemplateMilestone
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public required string Title { get; set; }
    public int DueOffsetDays { get; set; }
    public int SortOrder { get; set; }
    /// Comma-separated ProjectTemplateTask ids that roll up into this milestone.
    public string? TaskIds { get; set; }

    public ProjectTemplate Template { get; set; } = default!;
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
    /// Section (ProjectTemplateGroup) it lands in; null = the project's first section.
    public Guid? GroupId { get; set; }
    /// Comma-separated NIKs; applied when the template is used, only for users that still exist.
    public string? AssigneeNiks { get; set; }

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
    /// Optional owner; must be a project member when set.
    public Guid? OwnerId { get; set; }

    public Project Project { get; set; } = default!;
    public ProjectTask? Task { get; set; }
    public User? Owner { get; set; }
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
