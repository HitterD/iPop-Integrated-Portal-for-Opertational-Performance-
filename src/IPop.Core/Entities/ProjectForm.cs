namespace IPop.Core.Entities;

/// <summary>
/// Optional request form of one project (internal only: submitters are signed-in iPop users).
/// Answers become a task in the Intake group.
/// </summary>
public class ProjectForm
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public bool IsOpen { get; set; }
    public string Title { get; set; } = "";
    public string Intro { get; set; } = "";
    /// Who may submit: "project" (members) | "department" (same department as the owner) | "everyone" (any signed-in user).
    public string AudienceScope { get; set; } = "project";
    /// Null = the project owner.
    public Guid? AssigneeId { get; set; }
    public Guid? IntakeGroupId { get; set; }
    /// JSON [{key,label,type,required,options,mapsTo}], at most 12 questions.
    public string QuestionsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Project Project { get; set; } = default!;
}

/// <summary>One submitted request: links the signed-in submitter to the task it created (progress notifications, rate limit).</summary>
public class ProjectFormSubmission
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FormId { get; set; }
    public Guid SubmitterId { get; set; }
    public Guid TaskId { get; set; }
    public DateTime CreatedAt { get; set; }

    public ProjectForm Form { get; set; } = default!;
    public ProjectTask Task { get; set; } = default!;
}
