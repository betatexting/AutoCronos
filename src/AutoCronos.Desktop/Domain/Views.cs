namespace AutoCronos.Desktop.Domain;

public sealed record TaskCard(string CompanyName, string TaxId, string DeadlineLabel);
public sealed record KanbanColumn(string Name, IReadOnlyList<TaskCard> Cards);
public sealed record BoardModel(IReadOnlyList<KanbanColumn> Columns);
public sealed record WarningItem(Guid Id, string Title, string CompanyName, string TaxId, string Description, DateTime CreatedAtUtc);
public sealed record EmailSettingsSnapshot(string ClientId, string ClientSecret, string? ConnectedEmail, DateTime? LastSyncAtUtc);
public sealed record EmailConnectionStatus(bool IsConfigured, bool IsConnected, string StatusText, string DetailText, string? ConnectedEmail, DateTime? LastSyncAtUtc);
public sealed record EmailSyncSummary(bool Succeeded, bool Skipped, int MessagesScanned, int ProcessesCreated, int ApprovalsCreated, int IgnoredMessages, int FailedMessages, string Message);
