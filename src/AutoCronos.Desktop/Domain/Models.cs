namespace AutoCronos.Desktop.Domain;

public sealed record TaskCard(string CompanyName, string TaxId, string DeadlineLabel);
public sealed record KanbanColumn(string Name, IReadOnlyList<TaskCard> Cards);
public sealed record BoardModel(IReadOnlyList<KanbanColumn> Columns);
public enum WarningKind { Duplicate, Reactivation, CompetenceChange, MissingProcess }
public sealed record WarningItem(WarningKind Kind, string Title, string CompanyName, string TaxId, string Description, DateTime ReceivedAt);
