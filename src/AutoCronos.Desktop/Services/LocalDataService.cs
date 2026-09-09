using System.IO;
using System.Globalization;
using System.Text;
using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Application;
using AutoCronos.Desktop.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AutoCronos.Desktop.Services;

public sealed class LocalDataService
{
    private const string DevolutionOperationName = "Devolucoes";
    private const string InitialColumnName = "Informativo Recebido";
    private const string ReviewColumnName = "Conferencia";
    private const string StartDeactivationColumnName = "Iniciar Inativacao";
    private static readonly string[] SampleTaxIds = ["12345678000190", "98765432000110"];
    private readonly DbContextOptions<AutoCronosDbContext> _options;
    private readonly GmailEmailIntegrationService _gmail = new();
    private readonly SemaphoreSlim _databaseLock = new(1, 1);

    public LocalDataService()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCronos");
        Directory.CreateDirectory(directory);
        _options = new DbContextOptionsBuilder<AutoCronosDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "autocronos.db")}")
            .Options;
    }

    private Guid? _activeBoardId;

    public BoardModel Board { get; private set; } = new(Guid.Empty, string.Empty, []);
    public IReadOnlyList<BoardOption> Boards { get; private set; } = [];
    public IReadOnlyList<WarningItem> Warnings { get; private set; } = [];
    public event EventHandler? StateChanged;
    public event Action<AppNotification>? NotificationRaised;

    public IReadOnlyList<AppNotification> Initialize()
    {
        using var database = new AutoCronosDbContext(_options);
        database.Database.EnsureCreated();
        CreateExtensionTables(database);
        SeedDevolutionOperation(database);
        SeedLegacyCustomBoardFields(database);
        RemoveSampleData(database);
        RepairCompanyNames(database);
        var notifications = AdvanceDueCards(database, out _);
        RefreshViews(database);
        OnStateChanged();
        return notifications;
    }

    private static void CreateExtensionTables(AutoCronosDbContext database)
    {
        database.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "CardFieldDefinitions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CardFieldDefinitions" PRIMARY KEY,
                "OperationDefinitionId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "FieldType" INTEGER NOT NULL,
                "IsRequired" INTEGER NOT NULL,
                "ShowOnCard" INTEGER NOT NULL,
                "SortOrder" INTEGER NOT NULL,
                "EmailSource" INTEGER NOT NULL,
                CONSTRAINT "FK_CardFieldDefinitions_Operations_OperationDefinitionId"
                    FOREIGN KEY ("OperationDefinitionId") REFERENCES "Operations" ("Id") ON DELETE CASCADE
            );
            """);
        database.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "CardFieldValues" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CardFieldValues" PRIMARY KEY,
                "ProcessOccurrenceId" TEXT NOT NULL,
                "CardFieldDefinitionId" TEXT NOT NULL,
                "Value" TEXT NOT NULL,
                CONSTRAINT "FK_CardFieldValues_Occurrences_ProcessOccurrenceId"
                    FOREIGN KEY ("ProcessOccurrenceId") REFERENCES "Occurrences" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_CardFieldValues_CardFieldDefinitions_CardFieldDefinitionId"
                    FOREIGN KEY ("CardFieldDefinitionId") REFERENCES "CardFieldDefinitions" ("Id") ON DELETE CASCADE
            );
            """);
        database.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "EmailCardLinks" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_EmailCardLinks" PRIMARY KEY,
                "IncomingEmailId" TEXT NOT NULL,
                "ProcessOccurrenceId" TEXT NOT NULL,
                "OperationDefinitionId" TEXT NOT NULL,
                CONSTRAINT "FK_EmailCardLinks_IncomingEmails_IncomingEmailId"
                    FOREIGN KEY ("IncomingEmailId") REFERENCES "IncomingEmails" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_EmailCardLinks_Occurrences_ProcessOccurrenceId"
                    FOREIGN KEY ("ProcessOccurrenceId") REFERENCES "Occurrences" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_EmailCardLinks_Operations_OperationDefinitionId"
                    FOREIGN KEY ("OperationDefinitionId") REFERENCES "Operations" ("Id") ON DELETE CASCADE
            );
            """);
        database.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_CardFieldDefinitions_OperationDefinitionId_SortOrder\" ON \"CardFieldDefinitions\" (\"OperationDefinitionId\", \"SortOrder\");");
        database.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_CardFieldValues_ProcessOccurrenceId_CardFieldDefinitionId\" ON \"CardFieldValues\" (\"ProcessOccurrenceId\", \"CardFieldDefinitionId\");");
        database.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_EmailCardLinks_IncomingEmailId\" ON \"EmailCardLinks\" (\"IncomingEmailId\");");
        database.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_EmailCardLinks_ProcessOccurrenceId\" ON \"EmailCardLinks\" (\"ProcessOccurrenceId\");");
        database.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_EmailCardLinks_OperationDefinitionId\" ON \"EmailCardLinks\" (\"OperationDefinitionId\");");
    }

    public async Task ResolveApprovalAsync(Guid approvalId, bool approve, CancellationToken cancellationToken = default)
    {
        await _databaseLock.WaitAsync(cancellationToken);
        try
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
                await _gmail.MarkPendingMessageHandledAsync(providerMessageId, approve, cancellationToken);

            OnStateChanged();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public EmailConnectionStatus GetEmailStatus() => _gmail.GetStatus();

    public async Task<EmailConnectionStatus> ConnectEmailAsync(CancellationToken cancellationToken = default)
    {
        var status = await _gmail.ConnectAsync(cancellationToken);
        OnStateChanged();
        return status;
    }

    public async Task<EmailConnectionStatus> ConnectAnotherEmailAsync(CancellationToken cancellationToken = default)
    {
        var status = await _gmail.ConnectAnotherAccountAsync(cancellationToken);
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
        List<AppNotification> notifications;
        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            var subjectPatterns = LoadSubjectPatterns();
            var summary = await _gmail.SyncAsync(subjectPatterns, ProcessEmail, cancellationToken);
            using var database = new AutoCronosDbContext(_options);
            RepairCompanyNames(database);
            notifications = AdvanceDueCards(database, out _);
            RefreshViews(database);
            OnStateChanged();
            RaiseNotifications(notifications);
            return summary;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    private void RefreshViews(AutoCronosDbContext database)
    {
        var operations = database.Operations
            .Include(x => x.Columns)
            .Include(x => x.CardFields)
            .Include(x => x.Processes).ThenInclude(x => x.Occurrences).ThenInclude(x => x.FieldValues)
            .OrderBy(x => x.Name)
            .ToList();

        if (operations.Count == 0)
        {
            Board = new BoardModel(Guid.Empty, string.Empty, []);
            Boards = [];
            Warnings = [];
            return;
        }

        var operation = operations.FirstOrDefault(item => item.Id == _activeBoardId)
            ?? operations.FirstOrDefault(item => item.Name == DevolutionOperationName)
            ?? operations[0];
        _activeBoardId = operation.Id;

        Boards = operations
            .OrderByDescending(item => item.Name == DevolutionOperationName)
            .ThenBy(item => item.Name)
            .Select(item => new BoardOption(item.Id, item.Name, CanDelete: item.Name != DevolutionOperationName))
            .Append(new BoardOption(null, "+ Criar novo quadro...", IsCreateNew: true, CanDelete: false))
            .ToList();

        var orderedColumns = operation.Columns.OrderBy(x => x.SortOrder).ToList();
        var cardFields = operation.CardFields.OrderBy(field => field.SortOrder).ToList();
        var isDevolutionBoard = operation.Name == DevolutionOperationName;
        Board = new BoardModel(operation.Id, operation.Name, orderedColumns.Select((column, index) =>
        {
            var cards = operation.Processes
                .SelectMany(process => process.Occurrences.Where(occurrence => occurrence.CurrentColumn == column.Name), (process, occurrence) => new { process, occurrence })
                .OrderBy(x => x.occurrence.DeadlineAtUtc)
                .Select(x =>
                {
                    var displayValues = cardFields
                        .Where(field => field.ShowOnCard)
                        .Select(field => x.occurrence.FieldValues.FirstOrDefault(value => value.CardFieldDefinitionId == field.Id)?.Value)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Cast<string>()
                        .ToList();
                    var title = isDevolutionBoard ? x.process.CompanyName : displayValues.FirstOrDefault() ?? x.process.CompanyName;
                    var subtitle = isDevolutionBoard ? FormatTaxId(x.process.TaxId) : displayValues.Skip(1).FirstOrDefault() ?? string.Empty;
                    var card = new TaskCard(x.process.Id, x.occurrence.Id, title, subtitle, DeadlineLabel(x.occurrence), x.occurrence.ReceivedAtUtc, x.occurrence.CompletedAtUtc);
                    card.UpdateElapsedTime();
                    return card;
                })
                .ToList();
            return new KanbanColumn(column.Id, column.Name, index == 0 || SameColumn(column.Name, InitialColumnName), cards);
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
        if (database.Operations.Any(item => item.Name == DevolutionOperationName)) return;

        var operation = new OperationDefinition
        {
            Name = DevolutionOperationName,
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

    private static void SeedLegacyCustomBoardFields(AutoCronosDbContext database)
    {
        var legacyBoardIds = database.Operations
            .Where(operation => operation.Name != DevolutionOperationName && !operation.CardFields.Any())
            .Select(operation => operation.Id)
            .ToList();
        if (legacyBoardIds.Count == 0)
            return;

        database.CardFieldDefinitions.AddRange(legacyBoardIds.Select(operationId => new CardFieldDefinition
            {
                OperationDefinitionId = operationId,
                Name = "Titulo",
                FieldType = CardFieldType.Text,
                IsRequired = true,
                ShowOnCard = true,
                SortOrder = 1,
                EmailSource = EmailFieldSource.Subject
            }));
        database.SaveChanges();
    }

    private static void RemoveSampleData(AutoCronosDbContext database)
    {
        var sampleProcesses = database.Processes
            .Where(process => SampleTaxIds.Contains(process.TaxId))
            .ToList();
        var sampleEmails = database.IncomingEmails
            .Where(email => email.TaxId != null && SampleTaxIds.Contains(email.TaxId))
            .ToList();
        var sampleProcessIds = sampleProcesses.Select(process => process.Id).ToList();
        var sampleEmailIds = sampleEmails.Select(email => email.Id).ToList();

        if (sampleProcessIds.Count == 0 && sampleEmailIds.Count == 0)
            return;

        var sampleApprovals = database.Approvals
            .Where(approval =>
                (approval.ProcessId.HasValue && sampleProcessIds.Contains(approval.ProcessId.Value)) ||
                (approval.IncomingEmailId.HasValue && sampleEmailIds.Contains(approval.IncomingEmailId.Value)))
            .ToList();

        database.Approvals.RemoveRange(sampleApprovals);
        database.IncomingEmails.RemoveRange(sampleEmails);
        database.Processes.RemoveRange(sampleProcesses);
        database.SaveChanges();
    }

    public async Task SelectBoardAsync(Guid boardId, CancellationToken cancellationToken = default)
    {
        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            using var database = new AutoCronosDbContext(_options);
            if (!database.Operations.Any(item => item.Id == boardId))
                throw new InvalidOperationException("O quadro selecionado nao existe.");

            _activeBoardId = boardId;
            RefreshViews(database);
            OnStateChanged();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public IReadOnlyList<string> GetBoardColumns(Guid boardId)
    {
        using var database = new AutoCronosDbContext(_options);
        return database.KanbanColumns
            .Where(column => column.OperationDefinitionId == boardId)
            .OrderBy(column => column.SortOrder)
            .Select(column => column.Name)
            .ToList();
    }

    public BoardRulesEditor GetBoardRulesEditor(Guid boardId)
    {
        using var database = new AutoCronosDbContext(_options);
        var operation = database.Operations
            .Include(item => item.Columns)
            .Include(item => item.CardFields)
            .Include(item => item.EmailRules)
            .Include(item => item.DeadlineRules)
            .SingleOrDefault(item => item.Id == boardId)
            ?? throw new InvalidOperationException("O quadro selecionado nao existe.");
        if (operation.Name == DevolutionOperationName)
            throw new InvalidOperationException("As regras do quadro Devolucoes sao protegidas pelo fluxo contabil padrao.");

        var deadlineRule = operation.DeadlineRules
            .FirstOrDefault(rule => rule.EventType == EmailEventType.InitialNotice);
        return new BoardRulesEditor(
            operation.Id,
            operation.Name,
            operation.Columns.OrderBy(column => column.SortOrder).Select(column => column.Name).ToList(),
            operation.CardFields.OrderBy(field => field.SortOrder).Select(field => new CardFieldDefinitionInput(
                field.Name,
                field.FieldType,
                field.IsRequired,
                field.ShowOnCard,
                field.EmailSource,
                field.Id)).ToList(),
            operation.EmailRules.OrderBy(rule => rule.SubjectPattern).Select(rule => rule.SubjectPattern).ToList(),
            deadlineRule?.Amount,
            deadlineRule?.Unit,
            deadlineRule?.TargetColumnName);
    }

    public async Task<Guid> CreateBoardAsync(BoardCreationRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        var columnNames = request.ColumnNames
            .Select(column => column.Trim())
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var cardFields = request.CardFields
            .Select(field => field with { Name = field.Name.Trim() })
            .ToList();
        var subjectPatterns = request.EmailSubjectPatterns
            .Select(pattern => pattern.Trim())
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Informe o nome do quadro.");
        if (columnNames.Count == 0)
            throw new InvalidOperationException("Informe pelo menos uma coluna.");
        if (cardFields.Count == 0)
            throw new InvalidOperationException("Configure pelo menos um campo para os cards.");
        if (cardFields.Select(field => NormalizeName(field.Name)).Distinct().Count() != cardFields.Count)
            throw new InvalidOperationException("Os campos do card precisam ter nomes diferentes.");
        if (!cardFields.Any(field => field.ShowOnCard))
            throw new InvalidOperationException("Marque pelo menos um campo para ser exibido no card.");
        if (subjectPatterns.Count == 0)
            throw new InvalidOperationException("Configure pelo menos um padrao de assunto para captura de e-mail.");
        if (request.AutomaticMoveAmount is <= 0)
            throw new InvalidOperationException("O prazo automatico deve ser maior que zero.");
        if (request.AutomaticMoveAmount is not null && request.AutomaticMoveUnit is null)
            throw new InvalidOperationException("Selecione a unidade do prazo automatico.");
        if (request.AutomaticMoveAmount is not null &&
            !columnNames.Any(column => SameColumn(column, request.AutomaticMoveTargetColumn?.Trim() ?? string.Empty)))
            throw new InvalidOperationException("A coluna de destino automatico deve fazer parte do novo quadro.");

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            using var database = new AutoCronosDbContext(_options);
            if (database.Operations.Any(item => item.Name.ToUpper() == name.ToUpper()))
                throw new InvalidOperationException("Ja existe um quadro com esse nome.");
            var configuredPatterns = database.EmailRules.Select(rule => rule.SubjectPattern).ToList();
            var duplicatedPattern = subjectPatterns.FirstOrDefault(pattern =>
                configuredPatterns.Any(existing => NormalizeName(existing) == NormalizeName(pattern)));
            if (duplicatedPattern is not null)
                throw new InvalidOperationException($"O padrao de assunto '{duplicatedPattern}' ja pertence a outro quadro.");

            var operation = new OperationDefinition
            {
                Name = name,
                Columns = columnNames.Select((column, index) => new KanbanColumnDefinition
                {
                    Name = column,
                    SortOrder = index + 1,
                    IsTerminal = index == columnNames.Count - 1
                }).ToList(),
                EmailRules = subjectPatterns.Select(pattern => new EmailRule
                {
                    EventType = EmailEventType.InitialNotice,
                    SubjectPattern = pattern
                }).ToList(),
                CardFields = cardFields.Select((field, index) => new CardFieldDefinition
                {
                    Name = field.Name,
                    FieldType = field.FieldType,
                    IsRequired = field.IsRequired,
                    ShowOnCard = field.ShowOnCard,
                    EmailSource = field.EmailSource,
                    SortOrder = index + 1
                }).ToList()
            };
            if (request.AutomaticMoveAmount is { } amount)
            {
                operation.DeadlineRules.Add(new DeadlineRule
                {
                    EventType = EmailEventType.InitialNotice,
                    Unit = request.AutomaticMoveUnit!.Value,
                    Amount = amount,
                    TargetColumnName = request.AutomaticMoveTargetColumn!.Trim()
                });
            }

            database.Operations.Add(operation);
            database.SaveChanges();
            _activeBoardId = operation.Id;
            RefreshViews(database);
            OnStateChanged();
            return operation.Id;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task UpdateBoardRulesAsync(BoardRulesUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        var cardFields = request.CardFields
            .Select(field => field with { Name = field.Name.Trim() })
            .Where(field => !string.IsNullOrWhiteSpace(field.Name))
            .ToList();
        var subjectPatterns = request.EmailSubjectPatterns
            .Select(pattern => pattern.Trim())
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Informe o nome do quadro.");
        if (cardFields.Count == 0)
            throw new InvalidOperationException("Configure pelo menos um campo para os cards.");
        if (cardFields.Any(field => string.IsNullOrWhiteSpace(field.Name)))
            throw new InvalidOperationException("Informe o nome de todos os campos ou remova os campos vazios.");
        if (cardFields.Select(field => NormalizeName(field.Name)).Distinct().Count() != cardFields.Count)
            throw new InvalidOperationException("Os campos do card precisam ter nomes diferentes.");
        if (!cardFields.Any(field => field.ShowOnCard))
            throw new InvalidOperationException("Marque pelo menos um campo para ser exibido no card.");
        if (subjectPatterns.Count == 0)
            throw new InvalidOperationException("Configure pelo menos um padrao de assunto para captura de e-mail.");
        if (request.AutomaticMoveAmount is <= 0)
            throw new InvalidOperationException("O prazo automatico deve ser maior que zero.");
        if (request.AutomaticMoveAmount is not null && request.AutomaticMoveUnit is null)
            throw new InvalidOperationException("Selecione a unidade do prazo automatico.");
        if (cardFields.Where(field => field.Id.HasValue).Select(field => field.Id).Distinct().Count() != cardFields.Count(field => field.Id.HasValue))
            throw new InvalidOperationException("A lista de campos contem identificadores repetidos.");

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            using var database = new AutoCronosDbContext(_options);
            var operation = database.Operations
                .Include(item => item.Columns)
                .Include(item => item.CardFields)
                .Include(item => item.EmailRules)
                .Include(item => item.DeadlineRules)
                .SingleOrDefault(item => item.Id == request.BoardId)
                ?? throw new InvalidOperationException("O quadro selecionado nao existe.");
            if (operation.Name == DevolutionOperationName)
                throw new InvalidOperationException("As regras do quadro Devolucoes sao protegidas pelo fluxo contabil padrao.");
            if (database.Operations.Any(item => item.Id != operation.Id && item.Name.ToUpper() == name.ToUpper()))
                throw new InvalidOperationException("Ja existe um quadro com esse nome.");

            var existingFieldsById = operation.CardFields.ToDictionary(field => field.Id);
            var unknownField = cardFields.FirstOrDefault(field => field.Id is { } id && !existingFieldsById.ContainsKey(id));
            if (unknownField is not null)
                throw new InvalidOperationException($"O campo '{unknownField.Name}' nao pertence mais a este quadro. Reabra a tela de regras.");

            var configuredPatterns = database.EmailRules
                .Where(rule => rule.OperationDefinitionId != operation.Id)
                .Select(rule => rule.SubjectPattern)
                .ToList();
            var duplicatedPattern = subjectPatterns.FirstOrDefault(pattern =>
                configuredPatterns.Any(existing => NormalizeName(existing) == NormalizeName(pattern)));
            if (duplicatedPattern is not null)
                throw new InvalidOperationException($"O padrao de assunto '{duplicatedPattern}' ja pertence a outro quadro.");

            var targetColumnName = request.AutomaticMoveTargetColumn?.Trim();
            if (request.AutomaticMoveAmount is not null &&
                !operation.Columns.Any(column => SameColumn(column.Name, targetColumnName ?? string.Empty)))
                throw new InvalidOperationException("A coluna de destino automatico deve fazer parte do quadro.");

            using var transaction = database.Database.BeginTransaction();
            operation.Name = name;
            var retainedIds = cardFields.Where(field => field.Id.HasValue).Select(field => field.Id!.Value).ToHashSet();
            database.CardFieldDefinitions.RemoveRange(operation.CardFields.Where(field => !retainedIds.Contains(field.Id)));
            database.EmailRules.RemoveRange(operation.EmailRules);
            database.DeadlineRules.RemoveRange(operation.DeadlineRules.Where(rule => rule.EventType == EmailEventType.InitialNotice));

            foreach (var (field, index) in cardFields.Where(field => field.Id.HasValue).Select((field, index) => (field, index)))
            {
                var definition = existingFieldsById[field.Id!.Value];
                definition.Name = field.Name;
                definition.FieldType = field.FieldType;
                definition.IsRequired = field.IsRequired;
                definition.ShowOnCard = field.ShowOnCard;
                definition.EmailSource = field.EmailSource;
                definition.SortOrder = int.MinValue + index;
            }
            database.SaveChanges();

            foreach (var (field, index) in cardFields.Select((field, index) => (field, index)))
            {
                if (field.Id is { } fieldId)
                {
                    existingFieldsById[fieldId].SortOrder = index + 1;
                    continue;
                }

                database.CardFieldDefinitions.Add(new CardFieldDefinition
                {
                    OperationDefinitionId = operation.Id,
                    Name = field.Name,
                    FieldType = field.FieldType,
                    IsRequired = field.IsRequired,
                    ShowOnCard = field.ShowOnCard,
                    EmailSource = field.EmailSource,
                    SortOrder = index + 1
                });
            }
            database.EmailRules.AddRange(subjectPatterns.Select(pattern => new EmailRule
            {
                OperationDefinitionId = operation.Id,
                EventType = EmailEventType.InitialNotice,
                SubjectPattern = pattern
            }));
            if (request.AutomaticMoveAmount is { } amount)
            {
                database.DeadlineRules.Add(new DeadlineRule
                {
                    OperationDefinitionId = operation.Id,
                    EventType = EmailEventType.InitialNotice,
                    Unit = request.AutomaticMoveUnit!.Value,
                    Amount = amount,
                    TargetColumnName = targetColumnName!
                });
            }

            database.SaveChanges();
            transaction.Commit();
            RefreshViews(database);
            OnStateChanged();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task DeleteBoardAsync(Guid boardId, CancellationToken cancellationToken = default)
    {
        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            using var database = new AutoCronosDbContext(_options);
            var operation = database.Operations.SingleOrDefault(item => item.Id == boardId)
                ?? throw new InvalidOperationException("O quadro selecionado nao existe.");
            if (operation.Name == DevolutionOperationName)
                throw new InvalidOperationException("O quadro Devolucoes e necessario para a integracao de e-mail e nao pode ser excluido.");

            var processIds = database.Processes
                .Where(process => process.OperationDefinitionId == operation.Id)
                .Select(process => process.Id)
                .ToList();
            var approvals = database.Approvals
                .Where(approval => approval.ProcessId.HasValue && processIds.Contains(approval.ProcessId.Value))
                .ToList();
            var processes = database.Processes.Where(process => processIds.Contains(process.Id)).ToList();
            database.Approvals.RemoveRange(approvals);
            database.Processes.RemoveRange(processes);
            database.Operations.Remove(operation);
            database.SaveChanges();

            _activeBoardId = null;
            RefreshViews(database);
            OnStateChanged();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task AddColumnAsync(Guid boardId, string columnName, CancellationToken cancellationToken = default)
    {
        var name = columnName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Informe o nome da coluna.");

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            using var database = new AutoCronosDbContext(_options);
            var operation = database.Operations.Include(item => item.Columns).SingleOrDefault(item => item.Id == boardId)
                ?? throw new InvalidOperationException("O quadro selecionado nao existe.");
            if (operation.Columns.Any(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Ja existe uma coluna com esse nome neste quadro.");

            foreach (var terminal in operation.Columns.Where(column => column.IsTerminal))
                terminal.IsTerminal = false;
            database.KanbanColumns.Add(new KanbanColumnDefinition
            {
                OperationDefinitionId = operation.Id,
                Name = name,
                SortOrder = operation.Columns.Count == 0 ? 1 : operation.Columns.Max(column => column.SortOrder) + 1,
                IsTerminal = true
            });
            database.SaveChanges();
            RefreshViews(database);
            OnStateChanged();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task DeleteColumnAsync(Guid boardId, Guid columnId, CancellationToken cancellationToken = default)
    {
        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            using var database = new AutoCronosDbContext(_options);
            var operation = database.Operations
                .Include(item => item.Columns)
                .Include(item => item.Processes).ThenInclude(process => process.Occurrences)
                .Include(item => item.DeadlineRules)
                .SingleOrDefault(item => item.Id == boardId)
                ?? throw new InvalidOperationException("O quadro selecionado nao existe.");
            var column = operation.Columns.SingleOrDefault(item => item.Id == columnId)
                ?? throw new InvalidOperationException("A coluna selecionada nao existe.");
            if (operation.Columns.Count == 1)
                throw new InvalidOperationException("O quadro precisa manter pelo menos uma coluna.");
            if (operation.Processes.SelectMany(process => process.Occurrences).Any(occurrence => occurrence.CurrentColumn == column.Name))
                throw new InvalidOperationException("Mova ou exclua os cards desta coluna antes de remove-la.");

            database.DeadlineRules.RemoveRange(operation.DeadlineRules.Where(rule => SameColumn(rule.TargetColumnName, column.Name)));
            database.KanbanColumns.Remove(column);
            if (column.IsTerminal)
            {
                var newTerminal = operation.Columns
                    .Where(item => item.Id != column.Id)
                    .OrderByDescending(item => item.SortOrder)
                    .First();
                newTerminal.IsTerminal = true;
            }
            database.SaveChanges();
            RefreshViews(database);
            OnStateChanged();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<Guid> CreateManualTaskCardAsync(ManualTaskCardInput input, CancellationToken cancellationToken = default)
    {
        var companyName = input.CompanyName.Trim();
        var taxId = NormalizeTaxId(input.TaxId);
        if (string.IsNullOrWhiteSpace(companyName))
            throw new InvalidOperationException("Informe a razao social da empresa.");
        if (taxId is null)
            throw new InvalidOperationException("Informe um CPF ou CNPJ com 11 ou 14 digitos.");

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            using var database = new AutoCronosDbContext(_options);
            var operation = database.Operations
                .Include(item => item.Columns)
                .Include(item => item.DeadlineRules)
                .SingleOrDefault(item => item.Id == input.BoardId)
                ?? throw new InvalidOperationException("O quadro selecionado nao existe.");
            var initialColumn = operation.Columns.SingleOrDefault(column => SameColumn(column.Name, input.ColumnName))
                ?? throw new InvalidOperationException("A coluna inicial nao existe.");
            var process = database.Processes.Include(item => item.Occurrences)
                .SingleOrDefault(item => item.OperationDefinitionId == operation.Id && item.TaxId == taxId);
            if (process?.Occurrences.Any(occurrence => occurrence.Status == OccurrenceStatus.Active) == true)
                throw new InvalidOperationException("Ja existe um card ativo para este CPF/CNPJ neste quadro.");

            var isNewProcess = process is null;
            process ??= new Process
            {
                OperationDefinitionId = operation.Id,
                CompanyName = companyName,
                TaxId = taxId
            };
            process.CompanyName = companyName;
            var createdAtUtc = DateTime.UtcNow;
            var occurrence = new ProcessOccurrence
            {
                Number = process.Occurrences.Count + 1,
                CurrentColumn = initialColumn.Name,
                ReceivedAtUtc = createdAtUtc,
                Competence = string.IsNullOrWhiteSpace(input.Competence) ? null : input.Competence.Trim(),
                DeadlineAtUtc = input.DeadlineAtUtc ?? CalculateConfiguredDeadline(operation, createdAtUtc),
                History = [new ProcessHistoryEntry
                {
                    CreatedAtUtc = createdAtUtc,
                    EventType = "CardCriadoManualmente",
                    Description = "Card criado manualmente pelo usuario."
                }]
            };
            process.Occurrences.Add(occurrence);
            if (isNewProcess)
                database.Processes.Add(process);

            database.SaveChanges();
            RefreshViews(database);
            OnStateChanged();
            return occurrence.Id;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task MoveTaskCardAsync(Guid occurrenceId, string targetColumnName, CancellationToken cancellationToken = default)
    {
        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            using var database = new AutoCronosDbContext(_options);
            var card = database.Occurrences
                .AsNoTracking()
                .Where(item => item.Id == occurrenceId)
                .Select(item => new { item.ProcessId, item.CurrentColumn })
                .SingleOrDefault()
                ?? throw new InvalidOperationException("O card selecionado nao foi encontrado.");
            var processInfo = database.Processes
                .Where(process => process.Id == card.ProcessId)
                .Select(process => new { process.OperationDefinitionId, process.CompanyName })
                .Single();
            var operation = database.Operations
                .Include(item => item.Columns)
                .Single(item => item.Id == processInfo.OperationDefinitionId);
            var targetColumn = operation.Columns.SingleOrDefault(column => column.Name == targetColumnName)
                ?? throw new InvalidOperationException("A coluna de destino nao existe.");

            if (card.CurrentColumn == targetColumn.Name)
                return;

            var movedAtUtc = DateTime.UtcNow;
            var updatedRows = database.Occurrences
                .Where(item => item.Id == occurrenceId)
                .ExecuteUpdate(setters => setters
                    .SetProperty(item => item.CurrentColumn, targetColumn.Name)
                    .SetProperty(item => item.Status, targetColumn.IsTerminal ? OccurrenceStatus.Completed : OccurrenceStatus.Active)
                    .SetProperty(item => item.CompletedAtUtc, targetColumn.IsTerminal ? movedAtUtc : (DateTime?)null));
            if (updatedRows != 1)
                throw new InvalidOperationException("O card foi alterado por outra operacao. Atualize o quadro e tente novamente.");

            database.HistoryEntries.Add(new ProcessHistoryEntry
            {
                ProcessOccurrenceId = occurrenceId,
                CreatedAtUtc = movedAtUtc,
                EventType = "CardMovido",
                Description = $"Card movido de {card.CurrentColumn} para {targetColumn.Name}."
            });
            database.SaveChanges();
            RefreshViews(database);
            OnStateChanged();
            RaiseColumnNotification(targetColumn.Name, processInfo.CompanyName, automatic: false);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task DeleteTaskCardAsync(Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            string? providerMessageId;
            bool isManualCard;
            using (var lookupDatabase = new AutoCronosDbContext(_options))
            {
                var sourceOccurrence = lookupDatabase.Occurrences
                    .Include(item => item.Process)
                    .Include(item => item.History)
                    .SingleOrDefault(item => item.Id == occurrenceId)
                    ?? throw new InvalidOperationException("O card selecionado nao foi encontrado.");
                var sourceProcess = sourceOccurrence.Process ?? throw new InvalidOperationException("O processo do card nao foi encontrado.");
                isManualCard = sourceOccurrence.History.Any(item => item.EventType == "CardCriadoManualmente");
                providerMessageId = (from link in lookupDatabase.EmailCardLinks
                                     join email in lookupDatabase.IncomingEmails on link.IncomingEmailId equals email.Id
                                     where link.ProcessOccurrenceId == sourceOccurrence.Id
                                     orderby email.ReceivedAtUtc descending
                                     select email.ProviderMessageId)
                    .FirstOrDefault();
                providerMessageId ??= lookupDatabase.IncomingEmails
                    .Where(email => email.TaxId == sourceProcess.TaxId && email.ReceivedAtUtc == sourceOccurrence.ReceivedAtUtc)
                    .Select(email => email.ProviderMessageId)
                    .FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(providerMessageId) && !isManualCard)
                throw new InvalidOperationException("Nao foi possivel localizar o e-mail que originou este card. O card nao foi excluido.");

            if (!string.IsNullOrWhiteSpace(providerMessageId))
                await _gmail.MarkMessageIgnoredAsync(providerMessageId, cancellationToken);

            using var database = new AutoCronosDbContext(_options);
            var occurrence = database.Occurrences
                .Include(item => item.Process)
                .SingleOrDefault(item => item.Id == occurrenceId)
                ?? throw new InvalidOperationException("O card selecionado nao foi encontrado.");
            var process = occurrence.Process ?? throw new InvalidOperationException("O processo do card nao foi encontrado.");
            var occurrenceCount = database.Occurrences.Count(item => item.ProcessId == process.Id);

            if (occurrenceCount == 1)
            {
                var approvals = database.Approvals.Where(item => item.ProcessId == process.Id).ToList();
                database.Approvals.RemoveRange(approvals);
                database.Processes.Remove(process);
            }
            else
            {
                database.Occurrences.Remove(occurrence);
            }

            database.SaveChanges();
            RefreshViews(database);
            OnStateChanged();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public TaskCardDetails GetTaskCardDetails(Guid occurrenceId)
    {
        using var database = new AutoCronosDbContext(_options);
        var occurrence = database.Occurrences
            .Include(item => item.Process)
            .Include(item => item.History)
            .SingleOrDefault(item => item.Id == occurrenceId)
            ?? throw new InvalidOperationException("O card selecionado nao foi encontrado.");
        var process = occurrence.Process ?? throw new InvalidOperationException("O processo do card nao foi encontrado.");
        var columns = database.KanbanColumns
            .Where(column => column.OperationDefinitionId == process.OperationDefinitionId)
            .OrderBy(column => column.SortOrder)
            .Select(column => column.Name)
            .ToList();
        var subject = (from link in database.EmailCardLinks
                       join email in database.IncomingEmails on link.IncomingEmailId equals email.Id
                       where link.ProcessOccurrenceId == occurrence.Id
                       orderby email.ReceivedAtUtc descending
                       select email.Subject)
            .FirstOrDefault();
        subject ??= database.IncomingEmails
            .Where(email => email.TaxId == process.TaxId && email.ReceivedAtUtc == occurrence.ReceivedAtUtc)
            .Select(email => email.Subject)
            .FirstOrDefault() ?? (occurrence.History.Any(item => item.EventType == "CardCriadoManualmente")
                ? "Card criado manualmente."
                : "E-mail de origem nao localizado.");

        return new TaskCardDetails(occurrence.Id, process.CompanyName, process.TaxId, occurrence.Competence, occurrence.ReceivedAtUtc, occurrence.DeadlineAtUtc, occurrence.CurrentColumn, subject, columns);
    }

    public bool IsDevolutionBoard(Guid boardId)
    {
        using var database = new AutoCronosDbContext(_options);
        return database.Operations.Any(operation => operation.Id == boardId && operation.Name == DevolutionOperationName);
    }

    public CustomCardEditor GetCustomCardEditor(Guid boardId, Guid? occurrenceId = null, string? initialColumnName = null)
    {
        using var database = new AutoCronosDbContext(_options);
        var operation = database.Operations
            .Include(item => item.Columns)
            .Include(item => item.CardFields)
            .SingleOrDefault(item => item.Id == boardId)
            ?? throw new InvalidOperationException("O quadro selecionado nao existe.");
        if (operation.Name == DevolutionOperationName)
            throw new InvalidOperationException("O quadro Devolucoes utiliza o formulario contabil padrao.");

        var columns = operation.Columns.OrderBy(column => column.SortOrder).Select(column => column.Name).ToList();
        if (columns.Count == 0)
            throw new InvalidOperationException("O quadro nao possui colunas.");
        if (occurrenceId is null)
        {
            var initialColumn = columns.FirstOrDefault(column => SameColumn(column, initialColumnName ?? string.Empty)) ?? columns[0];
            return new CustomCardEditor(
                operation.Id,
                null,
                operation.Name,
                initialColumn,
                DateTime.UtcNow,
                null,
                null,
                columns,
                operation.CardFields.OrderBy(field => field.SortOrder)
                    .Select(field => new CustomCardFieldEditor(field.Id, field.Name, field.FieldType, field.IsRequired, string.Empty))
                    .ToList(),
                "Card criado manualmente.");
        }

        var occurrence = database.Occurrences
            .Include(item => item.Process)
            .Include(item => item.FieldValues)
            .SingleOrDefault(item => item.Id == occurrenceId && item.Process!.OperationDefinitionId == operation.Id)
            ?? throw new InvalidOperationException("O card selecionado nao foi encontrado neste quadro.");
        var subject = (from link in database.EmailCardLinks
                       join email in database.IncomingEmails on link.IncomingEmailId equals email.Id
                       where link.ProcessOccurrenceId == occurrence.Id
                       orderby email.ReceivedAtUtc descending
                       select email.Subject).FirstOrDefault() ?? "Card criado manualmente.";
        return new CustomCardEditor(
            operation.Id,
            occurrence.Id,
            operation.Name,
            occurrence.CurrentColumn,
            occurrence.ReceivedAtUtc,
            occurrence.CompletedAtUtc,
            occurrence.DeadlineAtUtc,
            columns,
            operation.CardFields.OrderBy(field => field.SortOrder)
                .Select(field => new CustomCardFieldEditor(
                    field.Id,
                    field.Name,
                    field.FieldType,
                    field.IsRequired,
                    occurrence.FieldValues.FirstOrDefault(value => value.CardFieldDefinitionId == field.Id)?.Value ?? string.Empty))
                .ToList(),
            subject);
    }

    public async Task<Guid> SaveCustomCardAsync(CustomCardEditor editor, CancellationToken cancellationToken = default)
    {
        ValidateCustomFields(editor.Fields);
        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            using var database = new AutoCronosDbContext(_options);
            var operation = database.Operations
                .Include(item => item.Columns)
                .Include(item => item.CardFields)
                .Include(item => item.DeadlineRules)
                .SingleOrDefault(item => item.Id == editor.BoardId)
                ?? throw new InvalidOperationException("O quadro selecionado nao existe.");
            if (operation.Name == DevolutionOperationName)
                throw new InvalidOperationException("Use o formulario contabil para cards de Devolucoes.");
            var targetColumn = operation.Columns.SingleOrDefault(column => SameColumn(column.Name, editor.CurrentColumn))
                ?? throw new InvalidOperationException("A coluna selecionada nao existe.");
            var definitions = operation.CardFields.OrderBy(field => field.SortOrder).ToList();
            if (editor.Fields.Any(field => definitions.All(definition => definition.Id != field.DefinitionId)))
                throw new InvalidOperationException("A configuracao dos campos deste quadro foi alterada. Reabra o card.");

            ProcessOccurrence occurrence;
            Process process;
            string? previousColumn = null;
            if (editor.OccurrenceId is { } occurrenceId)
            {
                occurrence = database.Occurrences
                    .Include(item => item.Process)
                    .Include(item => item.History)
                    .Include(item => item.FieldValues)
                    .SingleOrDefault(item => item.Id == occurrenceId)
                    ?? throw new InvalidOperationException("O card selecionado nao foi encontrado.");
                process = occurrence.Process ?? throw new InvalidOperationException("O processo do card nao foi encontrado.");
                if (process.OperationDefinitionId != operation.Id)
                    throw new InvalidOperationException("O card nao pertence ao quadro selecionado.");
                previousColumn = occurrence.CurrentColumn;
            }
            else
            {
                var now = DateTime.UtcNow;
                occurrence = new ProcessOccurrence
                {
                    Number = 1,
                    CurrentColumn = targetColumn.Name,
                    ReceivedAtUtc = now,
                    DeadlineAtUtc = editor.DeadlineAtUtc ?? CalculateConfiguredDeadline(operation, now),
                    History = [new ProcessHistoryEntry
                    {
                        CreatedAtUtc = now,
                        EventType = "CardCriadoManualmente",
                        Description = "Card criado manualmente pelo usuario."
                    }]
                };
                process = new Process
                {
                    OperationDefinitionId = operation.Id,
                    TaxId = $"MANUAL-{Guid.NewGuid():N}",
                    Occurrences = [occurrence]
                };
                database.Processes.Add(process);
            }

            foreach (var field in editor.Fields)
            {
                var value = occurrence.FieldValues.FirstOrDefault(item => item.CardFieldDefinitionId == field.DefinitionId);
                if (value is null)
                {
                    database.CardFieldValues.Add(new CardFieldValue
                    {
                        ProcessOccurrenceId = occurrence.Id,
                        CardFieldDefinitionId = field.DefinitionId,
                        Value = field.Value.Trim()
                    });
                }
                else
                {
                    value.Value = field.Value.Trim();
                }
            }

            process.CompanyName = definitions
                .Where(definition => definition.ShowOnCard)
                .OrderBy(definition => definition.SortOrder)
                .Select(definition => editor.Fields.FirstOrDefault(field => field.DefinitionId == definition.Id)?.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
                ?? $"Card {occurrence.Id.ToString()[..8]}";
            if (editor.OccurrenceId is not null)
                occurrence.DeadlineAtUtc = editor.DeadlineAtUtc;
            occurrence.CurrentColumn = targetColumn.Name;
            occurrence.Status = targetColumn.IsTerminal ? OccurrenceStatus.Completed : OccurrenceStatus.Active;
            occurrence.CompletedAtUtc = targetColumn.IsTerminal ? occurrence.CompletedAtUtc ?? DateTime.UtcNow : null;
            if (previousColumn is not null && !SameColumn(previousColumn, targetColumn.Name))
            {
                database.HistoryEntries.Add(new ProcessHistoryEntry
                {
                    ProcessOccurrenceId = occurrence.Id,
                    CreatedAtUtc = DateTime.UtcNow,
                    EventType = "CardEditado",
                    Description = $"Coluna alterada de {previousColumn} para {targetColumn.Name} pela edicao do card."
                });
            }

            database.SaveChanges();
            RefreshViews(database);
            OnStateChanged();
            if (previousColumn is not null && !SameColumn(previousColumn, targetColumn.Name))
                RaiseColumnNotification(targetColumn.Name, process.CompanyName, automatic: false);
            return occurrence.Id;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    private static void ValidateCustomFields(IEnumerable<CustomCardFieldEditor> fields)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        foreach (var field in fields)
        {
            var value = field.Value.Trim();
            if (field.IsRequired && string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Preencha o campo obrigatorio '{field.Name}'.");
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (field.FieldType == CardFieldType.Number &&
                !decimal.TryParse(value, NumberStyles.Number, culture, out _) &&
                !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                throw new InvalidOperationException($"O campo '{field.Name}' deve conter um numero valido.");
            if (field.FieldType == CardFieldType.Date && !DateTime.TryParse(value, culture, DateTimeStyles.None, out _))
                throw new InvalidOperationException($"O campo '{field.Name}' deve conter uma data valida.");
        }
    }

    public async Task SaveTaskCardDetailsAsync(TaskCardDetails details, CancellationToken cancellationToken = default)
    {
        var companyName = details.CompanyName.Trim();
        var taxId = new string(details.TaxId.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(companyName))
            throw new InvalidOperationException("Informe a razao social da empresa.");
        if (taxId.Length is not (11 or 14))
            throw new InvalidOperationException("Informe um CPF ou CNPJ com 11 ou 14 digitos.");

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            using var database = new AutoCronosDbContext(_options);
            var occurrence = database.Occurrences
                .Include(item => item.Process)
                .Include(item => item.History)
                .SingleOrDefault(item => item.Id == details.OccurrenceId)
                ?? throw new InvalidOperationException("O card selecionado nao foi encontrado.");
            var process = occurrence.Process ?? throw new InvalidOperationException("O processo do card nao foi encontrado.");
            var targetColumn = database.KanbanColumns
                .SingleOrDefault(column => column.OperationDefinitionId == process.OperationDefinitionId && column.Name == details.CurrentColumn)
                ?? throw new InvalidOperationException("A coluna selecionada nao existe.");
            var duplicateExists = database.Processes.Any(item => item.Id != process.Id && item.OperationDefinitionId == process.OperationDefinitionId && item.TaxId == taxId);
            if (duplicateExists)
                throw new InvalidOperationException("Ja existe um processo para este CPF/CNPJ.");
            var linkedEmailIds = database.EmailCardLinks
                .Where(link => link.ProcessOccurrenceId == occurrence.Id)
                .Select(link => link.IncomingEmailId)
                .ToList();
            var sourceEmails = linkedEmailIds.Count > 0
                ? database.IncomingEmails.Where(email => linkedEmailIds.Contains(email.Id)).ToList()
                : database.IncomingEmails
                    .Where(email => email.TaxId == process.TaxId && email.ReceivedAtUtc == occurrence.ReceivedAtUtc)
                    .ToList();

            var previousColumn = occurrence.CurrentColumn;
            process.CompanyName = companyName;
            process.TaxId = taxId;
            foreach (var sourceEmail in sourceEmails)
            {
                sourceEmail.CompanyName = companyName;
                sourceEmail.TaxId = taxId;
            }
            occurrence.Competence = string.IsNullOrWhiteSpace(details.Competence) ? null : details.Competence.Trim();
            occurrence.DeadlineAtUtc = details.DeadlineAtUtc;
            occurrence.CurrentColumn = targetColumn.Name;
            occurrence.Status = targetColumn.IsTerminal ? OccurrenceStatus.Completed : OccurrenceStatus.Active;
            occurrence.CompletedAtUtc = targetColumn.IsTerminal ? occurrence.CompletedAtUtc ?? DateTime.UtcNow : null;
            if (previousColumn != targetColumn.Name)
            {
                database.HistoryEntries.Add(new ProcessHistoryEntry
                {
                    ProcessOccurrenceId = occurrence.Id,
                    CreatedAtUtc = DateTime.UtcNow,
                    EventType = "CardEditado",
                    Description = $"Coluna alterada de {previousColumn} para {targetColumn.Name} pela edicao do card."
                });
            }

            database.SaveChanges();
            RefreshViews(database);
            OnStateChanged();
            if (previousColumn != targetColumn.Name)
                RaiseColumnNotification(targetColumn.Name, process.CompanyName, automatic: false);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<int> AdvanceDueCardsAsync(CancellationToken cancellationToken = default)
    {
        List<AppNotification> notifications;
        int movedCount;
        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            using var database = new AutoCronosDbContext(_options);
            notifications = AdvanceDueCards(database, out movedCount);
            if (movedCount > 0)
            {
                RefreshViews(database);
                OnStateChanged();
            }
        }
        finally
        {
            _databaseLock.Release();
        }

        RaiseNotifications(notifications);
        return movedCount;
    }

    private static List<AppNotification> AdvanceDueCards(AutoCronosDbContext database, out int movedCount)
    {
        var notifications = new List<AppNotification>();
        movedCount = 0;
        var nowUtc = DateTime.UtcNow;
        var today = DateTime.Today;
        var operations = database.Operations
            .Include(operation => operation.Columns)
            .Include(operation => operation.DeadlineRules)
            .Include(operation => operation.Processes).ThenInclude(process => process.Occurrences).ThenInclude(occurrence => occurrence.History)
            .ToList();

        foreach (var operation in operations)
        {
            var rule = operation.DeadlineRules
                .Where(item => item.EventType == EmailEventType.InitialNotice)
                .OrderBy(item => item.Amount)
                .FirstOrDefault();
            if (rule is null)
                continue;

            var targetColumn = operation.Columns.FirstOrDefault(column => SameColumn(column.Name, rule.TargetColumnName));
            if (targetColumn is null)
                continue;

            foreach (var process in operation.Processes)
            {
                foreach (var occurrence in process.Occurrences.Where(item =>
                             item.Status == OccurrenceStatus.Active &&
                             item.DeadlineAtUtc.HasValue &&
                             (rule.Unit == DeadlineUnit.Hours
                                 ? item.DeadlineAtUtc.Value <= nowUtc
                                 : item.DeadlineAtUtc.Value.ToLocalTime().Date <= today)))
                {
                    var currentColumn = operation.Columns.FirstOrDefault(column => SameColumn(column.Name, occurrence.CurrentColumn));
                    if (currentColumn is not null && currentColumn.SortOrder >= targetColumn.SortOrder)
                        continue;

                    var previousColumn = occurrence.CurrentColumn;
                    occurrence.CurrentColumn = targetColumn.Name;
                    occurrence.Status = targetColumn.IsTerminal ? OccurrenceStatus.Completed : OccurrenceStatus.Active;
                    occurrence.CompletedAtUtc = targetColumn.IsTerminal ? DateTime.UtcNow : null;
                    database.HistoryEntries.Add(new ProcessHistoryEntry
                    {
                        ProcessOccurrenceId = occurrence.Id,
                        CreatedAtUtc = DateTime.UtcNow,
                        EventType = "PrazoAtingido",
                        Description = $"Card movido automaticamente de {previousColumn} para {targetColumn.Name} ao atingir o prazo."
                    });
                    movedCount++;
                    var notification = BuildColumnNotification(targetColumn.Name, process.CompanyName, automatic: true);
                    if (notification is not null)
                        notifications.Add(notification);
                }
            }
        }

        if (database.ChangeTracker.HasChanges())
            database.SaveChanges();
        return notifications;
    }

    private static DateTime? CalculateConfiguredDeadline(OperationDefinition operation, DateTime createdAtUtc)
    {
        var rule = operation.DeadlineRules.FirstOrDefault(item => item.EventType == EmailEventType.InitialNotice);
        if (rule is null)
            return null;
        return rule.Unit switch
        {
            DeadlineUnit.Hours => createdAtUtc.AddHours(rule.Amount),
            DeadlineUnit.CalendarDays => createdAtUtc.AddDays(rule.Amount),
            DeadlineUnit.BusinessDays => AddBusinessDays(createdAtUtc, rule.Amount),
            _ => null
        };
    }

    private static DateTime AddBusinessDays(DateTime initial, int days)
    {
        var result = initial;
        while (days > 0)
        {
            result = result.AddDays(1);
            if (result.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                days--;
        }
        return result;
    }

    private static void RepairCompanyNames(AutoCronosDbContext database)
    {
        var emailsByTaxId = database.IncomingEmails
            .Where(email => email.TaxId != null && email.Subject != null)
            .OrderByDescending(email => email.ReceivedAtUtc)
            .ToList()
            .GroupBy(email => email.TaxId!)
            .ToDictionary(group => group.Key, group => group.Select(email => EmailCompanyNameExtractor.Extract(email.Subject, string.Empty)).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
        var changed = false;

        foreach (var process in database.Processes)
        {
            var currentNameIsTaxId = new string(process.CompanyName.Where(char.IsDigit).ToArray()) == process.TaxId;
            var storedName = EmailCompanyNameExtractor.ExtractCompanyNameBeforeTaxId(process.CompanyName);
            var currentNameIncludesTaxId = storedName is not null && !string.Equals(storedName, process.CompanyName, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(process.CompanyName) && !currentNameIsTaxId && !currentNameIncludesTaxId)
                continue;
            var companyName = emailsByTaxId.TryGetValue(process.TaxId, out var extractedName) && !string.IsNullOrWhiteSpace(extractedName)
                ? extractedName
                : storedName;
            if (string.IsNullOrWhiteSpace(companyName))
                continue;

            process.CompanyName = companyName;
            changed = true;
        }

        if (changed)
            database.SaveChanges();
    }

    private static string DeadlineLabel(ProcessOccurrence occurrence)
    {
        if (occurrence.DeadlineAtUtc is null)
            return "Sem prazo configurado";
        var localDeadline = occurrence.DeadlineAtUtc.Value.ToLocalTime();
        return localDeadline.TimeOfDay == TimeSpan.Zero
            ? $"Prazo: {localDeadline:dd/MM/yyyy}"
            : $"Prazo: {localDeadline:dd/MM/yyyy HH:mm}";
    }

    private static string FormatTaxId(string taxId) => taxId.Length switch
    {
        14 => $"{taxId[..2]}.{taxId[2..5]}.{taxId[5..8]}/{taxId[8..12]}-{taxId[12..]}",
        11 => $"{taxId[..3]}.{taxId[3..6]}.{taxId[6..9]}-{taxId[9..]}",
        _ => taxId
    };

    private static string? NormalizeTaxId(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length is 11 or 14 ? digits : null;
    }

    private static bool SameColumn(string left, string right) =>
        string.Equals(NormalizeName(left), NormalizeName(right), StringComparison.Ordinal);

    private static string NormalizeName(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && !char.IsWhiteSpace(character))
                builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }

    private void RaiseColumnNotification(string columnName, string companyName, bool automatic)
    {
        var notification = BuildColumnNotification(columnName, companyName, automatic);
        if (notification is not null)
            NotificationRaised?.Invoke(notification);
    }

    private void RaiseNotifications(IEnumerable<AppNotification> notifications)
    {
        foreach (var notification in notifications)
            NotificationRaised?.Invoke(notification);
    }

    private static AppNotification? BuildColumnNotification(string columnName, string companyName, bool automatic)
    {
        if (SameColumn(columnName, ReviewColumnName))
            return new AppNotification("Card em Conferencia", $"{companyName} foi adicionado a Conferencia.", automatic);
        if (SameColumn(columnName, StartDeactivationColumnName))
        {
            var action = automatic ? "foi movido automaticamente" : "foi movido";
            return new AppNotification("Iniciar inativacao", $"{companyName} {action} para Iniciar Inativacao.", automatic);
        }
        return null;
    }

    private IReadOnlyList<string> LoadSubjectPatterns()
    {
        using var database = new AutoCronosDbContext(_options);
        return database.Operations
            .Include(x => x.EmailRules)
            .Where(x => x.IsActive)
            .SelectMany(x => x.EmailRules)
            .Select(x => x.SubjectPattern)
            .Distinct()
            .ToList();
    }

    private EmailProcessingResult ProcessEmail(EmailInput input)
    {
        using var database = new AutoCronosDbContext(_options);
        return new BoardEmailProcessor(database).Process(input);
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
