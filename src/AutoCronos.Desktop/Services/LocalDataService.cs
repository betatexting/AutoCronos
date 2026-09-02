using System.IO;
using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Application;
using AutoCronos.Desktop.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AutoCronos.Desktop.Services;

public sealed class LocalDataService
{
    private readonly DbContextOptions<AutoCronosDbContext> _options;
    private readonly GmailEmailIntegrationService _gmail = new();

    public LocalDataService()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCronos");
        Directory.CreateDirectory(directory);
        _options = new DbContextOptionsBuilder<AutoCronosDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "autocronos.db")}")
            .Options;
    }

    public BoardModel Board { get; private set; } = new([]);
    public IReadOnlyList<WarningItem> Warnings { get; private set; } = [];
    public event EventHandler? StateChanged;

    public void Initialize()
    {
        using var database = new AutoCronosDbContext(_options);
        database.Database.EnsureCreated();
        SeedDevolutionOperation(database);
        RefreshViews(database);
        OnStateChanged();
    }

    public async Task ResolveApprovalAsync(Guid approvalId, bool approve, CancellationToken cancellationToken = default)
    {
        string? providerMessageId;
        using var database = new AutoCronosDbContext(_options);
        var incomingEmailId = database.Approvals.Where(x => x.Id == approvalId).Select(x => x.IncomingEmailId).SingleOrDefault();
        providerMessageId = incomingEmailId is null
            ? null
            : database.IncomingEmails.Where(x => x.Id == incomingEmailId).Select(x => x.ProviderMessageId).SingleOrDefault();

        new ApprovalDecisionService(database).Resolve(approvalId, approve);
        RefreshViews(database);
        if (!string.IsNullOrWhiteSpace(providerMessageId))
            await _gmail.MarkPendingMessageHandledAsync(providerMessageId, cancellationToken);

        OnStateChanged();
    }

    public EmailSettingsSnapshot GetEmailSettings() => _gmail.GetSettingsSnapshot();

    public EmailConnectionStatus GetEmailStatus() => _gmail.GetStatus();

    public void SaveEmailSettings(string clientId, string clientSecret)
    {
        _gmail.SaveSettings(clientId, clientSecret);
        OnStateChanged();
    }

    public async Task<EmailConnectionStatus> ConnectEmailAsync(CancellationToken cancellationToken = default)
    {
        var status = await _gmail.ConnectAsync(cancellationToken);
        OnStateChanged();
        return status;
    }

    public async Task<EmailConnectionStatus> DisconnectEmailAsync(CancellationToken cancellationToken = default)
    {
        var status = await _gmail.DisconnectAsync(cancellationToken);
        OnStateChanged();
        return status;
    }

    public async Task<EmailSyncSummary> SyncEmailAsync(CancellationToken cancellationToken = default)
    {
        var subjectPatterns = LoadSubjectPatterns();
        var summary = await _gmail.SyncAsync(subjectPatterns, ProcessEmail, cancellationToken);
        using var database = new AutoCronosDbContext(_options);
        RefreshViews(database);
        OnStateChanged();
        return summary;
    }

    private void RefreshViews(AutoCronosDbContext database)
    {
        var operation = database.Operations
            .Include(x => x.Columns)
            .Include(x => x.Processes).ThenInclude(x => x.Occurrences)
            .Single(x => x.Name == "Devolucoes");

        Board = new BoardModel(operation.Columns.OrderBy(x => x.SortOrder).Select(column =>
        {
            var cards = operation.Processes
                .SelectMany(process => process.Occurrences.Where(occurrence => occurrence.CurrentColumn == column.Name), (process, occurrence) => new { process, occurrence })
                .OrderBy(x => x.occurrence.DeadlineAtUtc)
                .Select(x => new TaskCard(x.process.CompanyName, FormatTaxId(x.process.TaxId), DeadlineLabel(x.occurrence)))
                .ToList();
            return new KanbanColumn(column.Name, cards);
        }).ToList());

        var processes = database.Processes.ToDictionary(x => x.Id);
        Warnings = database.Approvals.Where(x => x.Status == ApprovalStatus.Pending).OrderBy(x => x.CreatedAtUtc)
            .AsEnumerable().Select(approval =>
            {
                processes.TryGetValue(approval.ProcessId ?? Guid.Empty, out var process);
                return new WarningItem(approval.Id, approval.Title, process?.CompanyName ?? "Processo nao localizado", process is null ? "-" : FormatTaxId(process.TaxId), approval.Description, approval.CreatedAtUtc);
            }).ToList();
    }

    private static void SeedDevolutionOperation(AutoCronosDbContext database)
    {
        if (database.Operations.Any()) return;

        var operation = new OperationDefinition
        {
            Name = "Devolucoes",
            Columns =
            [
                new() { Name = "Informativo Recebido", SortOrder = 1 },
                new() { Name = "Conferencia", SortOrder = 2 },
                new() { Name = "Iniciar Inativacao", SortOrder = 3 },
                new() { Name = "Em Andamento", SortOrder = 4 },
                new() { Name = "Concluido", SortOrder = 5, IsTerminal = true }
            ],
            EmailRules =
            [
                new() { EventType = EmailEventType.InitialNotice, SubjectPattern = "COMUNICADO GERAL DE DEVOLUCAO" },
                new() { EventType = EmailEventType.InitialNotice, SubjectPattern = "COMUNICADO DE DEVOLUCAO" },
                new() { EventType = EmailEventType.InitialNotice, SubjectPattern = "INFORMATIVO DE DEVOLUCAO" },
                new() { EventType = EmailEventType.CompetenceChange, SubjectPattern = "ALTERACAO DE COMPETENCIA - INFORMATIVO DE DEVOLUCAO" }
            ],
            DeadlineRules = [new() { EventType = EmailEventType.InitialNotice, Unit = DeadlineUnit.CalendarDays, Amount = 30, TargetColumnName = "Iniciar Inativacao" }]
        };
        database.Operations.Add(operation);
        database.SaveChanges();
    }

    private static string DeadlineLabel(ProcessOccurrence occurrence) => occurrence.DeadlineAtUtc is null
        ? "Sem prazo configurado"
        : $"Prazo: {occurrence.DeadlineAtUtc.Value.ToLocalTime():dd/MM/yyyy}";

    private static string FormatTaxId(string taxId) => taxId.Length == 14
        ? $"{taxId[..2]}.{taxId[2..5]}.{taxId[5..8]}/{taxId[8..12]}-{taxId[12..]}"
        : taxId;

    private IReadOnlyList<string> LoadSubjectPatterns()
    {
        using var database = new AutoCronosDbContext(_options);
        return database.Operations
            .Include(x => x.EmailRules)
            .Where(x => x.Name == "Devolucoes" && x.IsActive)
            .SelectMany(x => x.EmailRules)
            .Select(x => x.SubjectPattern)
            .Distinct()
            .ToList();
    }

    private EmailProcessingResult ProcessEmail(EmailInput input)
    {
        using var database = new AutoCronosDbContext(_options);
        return new DevolutionEmailProcessor(database).Process(input);
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
