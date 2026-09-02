namespace AutoCronos.Desktop.Domain;

public enum EmailEventType { InitialNotice, CompetenceChange }
public enum DeadlineUnit { Hours, CalendarDays, BusinessDays }
public enum OccurrenceStatus { Active, Completed }
public enum ApprovalType { DuplicateNotice, ReactivateProcess, ChangeCompetence, MissingProcess }
public enum ApprovalStatus { Pending, Approved, Rejected }

public sealed class OperationDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public List<KanbanColumnDefinition> Columns { get; set; } = [];
    public List<EmailRule> EmailRules { get; set; } = [];
    public List<DeadlineRule> DeadlineRules { get; set; } = [];
    public List<Process> Processes { get; set; } = [];
}

public sealed class KanbanColumnDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationDefinitionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsTerminal { get; set; }
}

public sealed class EmailRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationDefinitionId { get; set; }
    public EmailEventType EventType { get; set; }
    public string SubjectPattern { get; set; } = string.Empty;
}

public sealed class DeadlineRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationDefinitionId { get; set; }
    public EmailEventType EventType { get; set; }
    public DeadlineUnit Unit { get; set; }
    public int Amount { get; set; }
    public string TargetColumnName { get; set; } = string.Empty;
}

public sealed class Process
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationDefinitionId { get; set; }
    public string TaxId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public OperationDefinition? Operation { get; set; }
    public List<ProcessOccurrence> Occurrences { get; set; } = [];
}

public sealed class ProcessOccurrence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProcessId { get; set; }
    public int Number { get; set; }
    public OccurrenceStatus Status { get; set; } = OccurrenceStatus.Active;
    public string CurrentColumn { get; set; } = string.Empty;
    public string? Competence { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? DeadlineAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public Process? Process { get; set; }
    public List<ProcessHistoryEntry> History { get; set; } = [];
}

public sealed class ProcessHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProcessOccurrenceId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class IncomingEmail
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderMessageId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string? TaxId { get; set; }
    public string? CompanyName { get; set; }
    public string? Competence { get; set; }
    public EmailEventType? DetectedEventType { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public bool IsProcessed { get; set; }
    public bool IsRejectedAsDuplicate { get; set; }
}

public sealed class ApprovalItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ApprovalType Type { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public Guid? ProcessId { get; set; }
    public Guid? IncomingEmailId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
