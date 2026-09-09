using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AutoCronos.Desktop.Domain;

public sealed class TaskCard(Guid processId, Guid occurrenceId, string companyName, string taxId, string deadlineLabel, DateTime receivedAtUtc, DateTime? completedAtUtc) : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _elapsedLabel = string.Empty;

    public Guid ProcessId { get; } = processId;
    public Guid OccurrenceId { get; } = occurrenceId;
    public string CompanyName { get; } = companyName;
    public string DisplayCompanyName { get; } = CompactCompanyName(companyName);
    public string TaxId { get; } = taxId;
    public string DeadlineLabel { get; } = deadlineLabel;
    public DateTime ReceivedAtUtc { get; } = receivedAtUtc;
    public DateTime? CompletedAtUtc { get; } = completedAtUtc;
    public string ElapsedLabel
    {
        get => _elapsedLabel;
        private set
        {
            if (_elapsedLabel == value)
                return;
            _elapsedLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ElapsedLabel)));
        }
    }

    public void UpdateElapsedTime()
    {
        var elapsed = (CompletedAtUtc ?? DateTime.UtcNow) - ReceivedAtUtc;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        ElapsedLabel = elapsed.TotalDays >= 1
            ? $"Tempo: {(int)elapsed.TotalDays}d {elapsed.Hours}h"
            : $"Tempo: {(int)elapsed.TotalHours}h {elapsed.Minutes}min";
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string CompactCompanyName(string value)
    {
        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        var extractedName = EmailCompanyNameBeforeTaxId(compact);
        return extractedName.Length <= 90 ? extractedName : $"{extractedName[..87].TrimEnd()}...";
    }

    private static string EmailCompanyNameBeforeTaxId(string value)
    {
        var taxIdMatch = Regex.Match(value, @"(?<!\d)(?:\d{2}\.?\d{3}\.?\d{3}[\/-]?\d{4}-?\d{2})(?!\d)");
        var name = taxIdMatch.Success ? value[..taxIdMatch.Index].Trim().TrimEnd('-', '\u2013', '\u2014', ' ') : value;
        return string.IsNullOrWhiteSpace(name) ? value : name;
    }
}

public sealed record KanbanColumn(Guid Id, string Name, bool AllowsManualCard, IReadOnlyList<TaskCard> Cards);
public sealed record BoardModel(Guid Id, string Name, IReadOnlyList<KanbanColumn> Columns);
public sealed record BoardOption(Guid? Id, string Name, bool IsCreateNew = false, bool CanDelete = true);
public sealed record CardFieldDefinitionInput(string Name, CardFieldType FieldType, bool IsRequired, bool ShowOnCard, EmailFieldSource EmailSource, Guid? Id = null);
public sealed record BoardCreationRequest(string Name, IReadOnlyList<string> ColumnNames, IReadOnlyList<CardFieldDefinitionInput> CardFields, IReadOnlyList<string> EmailSubjectPatterns, int? AutomaticMoveAmount, DeadlineUnit? AutomaticMoveUnit, string? AutomaticMoveTargetColumn);
public sealed record BoardRulesUpdateRequest(Guid BoardId, string Name, IReadOnlyList<CardFieldDefinitionInput> CardFields, IReadOnlyList<string> EmailSubjectPatterns, int? AutomaticMoveAmount, DeadlineUnit? AutomaticMoveUnit, string? AutomaticMoveTargetColumn);
public sealed record BoardRulesEditor(Guid BoardId, string Name, IReadOnlyList<string> ColumnNames, IReadOnlyList<CardFieldDefinitionInput> CardFields, IReadOnlyList<string> EmailSubjectPatterns, int? AutomaticMoveAmount, DeadlineUnit? AutomaticMoveUnit, string? AutomaticMoveTargetColumn);
public sealed record ManualTaskCardInput(Guid BoardId, string ColumnName, string CompanyName, string TaxId, string? Competence, DateTime? DeadlineAtUtc);
public sealed record CustomCardFieldEditor(Guid DefinitionId, string Name, CardFieldType FieldType, bool IsRequired, string Value);
public sealed record CustomCardEditor(Guid BoardId, Guid? OccurrenceId, string BoardName, string CurrentColumn, DateTime ReceivedAtUtc, DateTime? CompletedAtUtc, DateTime? DeadlineAtUtc, IReadOnlyList<string> Columns, IReadOnlyList<CustomCardFieldEditor> Fields, string EmailSubject);
public sealed record TaskCardDetails(Guid OccurrenceId, string CompanyName, string TaxId, string? Competence, DateTime ReceivedAtUtc, DateTime? DeadlineAtUtc, string CurrentColumn, string EmailSubject, IReadOnlyList<string> Columns);
public sealed record WarningItem(Guid Id, string Title, string CompanyName, string TaxId, string Description, DateTime CreatedAtUtc);
public sealed record EmailConnectionStatus(bool IsConfigured, bool IsConnected, string StatusText, string DetailText, string? ConnectedEmail, DateTime? LastSyncAtUtc);
public sealed record EmailSyncSummary(bool Succeeded, bool Skipped, int MessagesScanned, int ProcessesCreated, int ApprovalsCreated, int IgnoredMessages, int FailedMessages, string Message);
public sealed record AppNotification(string Title, string Message, bool IsPersistent = false);
