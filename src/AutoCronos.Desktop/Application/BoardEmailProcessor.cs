using System.Globalization;
using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AutoCronos.Desktop.Application;

/// <summary>Routes each message to one configured board before applying its card rules.</summary>
public sealed class BoardEmailProcessor(AutoCronosDbContext database)
{
    private const string DevolutionOperationName = "Devolucoes";

    public EmailProcessingResult Process(EmailInput input)
    {
        if (database.IncomingEmails.Any(email => email.ProviderMessageId == input.ProviderMessageId))
            return new EmailProcessingResult(true, false, null, "E-mail ja processado.");

        if (input.IsReply)
        {
            var email = IncomingEmailFactory.Create(input, null);
            database.IncomingEmails.Add(email);

            if (input.IsFromConnectedAccount)
            {
                database.SaveChanges();
                return new EmailProcessingResult(false, false, null, "Resposta enviada pela conta conectada registrada sem criar card.");
            }

            var context = FindReplyContext(input);
            var approval = new ApprovalItem
            {
                Type = ApprovalType.ReplyAudit,
                ProcessId = context.ProcessId,
                ProcessOccurrenceId = context.OccurrenceId,
                IncomingEmailId = email.Id,
                Title = "Resposta de colaborador para auditoria",
                Description = context.ProcessId is null
                    ? $"Resposta recebida de {input.Sender}. A mensagem original ou processo relacionado nao foi localizado automaticamente; revise antes de aprovar ou recusar."
                    : $"Resposta recebida de {input.Sender} e vinculada ao processo localizado na conversa. Revise antes de aprovar ou recusar.",
                CreatedAtUtc = DateTime.UtcNow
            };
            database.Approvals.Add(approval);
            database.SaveChanges();
            return new EmailProcessingResult(false, false, approval.Id, approval.Title);
        }

        var normalizedSubject = DomainText.NormalizeSubject(input.Subject);
        var operations = database.Operations
            .Include(operation => operation.EmailRules)
            .Include(operation => operation.Columns)
            .Include(operation => operation.DeadlineRules)
            .Include(operation => operation.CardFields)
            .Where(operation => operation.IsActive)
            .ToList();
        var matches = operations
            .SelectMany(operation => operation.EmailRules.Select(rule => new
            {
                Operation = operation,
                Pattern = DomainText.NormalizeSubject(rule.SubjectPattern)
            }))
            .Where(match => match.Pattern.Length > 0 && normalizedSubject.Contains(match.Pattern, StringComparison.Ordinal))
            .OrderByDescending(match => match.Pattern.Length)
            .ToList();

        if (matches.Count == 0)
        {
            database.IncomingEmails.Add(IncomingEmailFactory.Create(input, null));
            database.SaveChanges();
            return new EmailProcessingResult(false, false, null, "E-mail registrado, mas sem regra reconhecida para qualquer quadro.");
        }

        var bestLength = matches[0].Pattern.Length;
        var bestOperations = matches
            .Where(match => match.Pattern.Length == bestLength)
            .Select(match => match.Operation)
            .DistinctBy(operation => operation.Id)
            .ToList();
        var operation = bestOperations.Count == 1
            ? bestOperations[0]
            : bestOperations.SingleOrDefault(candidate => candidate.Name == DevolutionOperationName);
        if (operation is null)
        {
            database.IncomingEmails.Add(IncomingEmailFactory.Create(input, null));
            database.SaveChanges();
            return new EmailProcessingResult(false, false, null, "E-mail registrado, mas o assunto corresponde a mais de um quadro com a mesma prioridade.");
        }

        if (operation.Name == DevolutionOperationName)
            return new DevolutionEmailProcessor(database).Process(input);

        return CreateCustomBoardCard(operation, input);
    }

    private (Guid? ProcessId, Guid? OccurrenceId) FindReplyContext(EmailInput input)
    {
        var relatedMessageIds = input.ConversationMessageIds
            .Where(id => !string.Equals(id, input.ProviderMessageId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (relatedMessageIds.Count > 0)
        {
            var linkedContext = (from email in database.IncomingEmails
                                 join link in database.EmailCardLinks on email.Id equals link.IncomingEmailId
                                 join linkedOccurrence in database.Occurrences on link.ProcessOccurrenceId equals linkedOccurrence.Id
                                 where relatedMessageIds.Contains(email.ProviderMessageId)
                                 orderby email.ReceivedAtUtc descending
                                 select new { linkedOccurrence.ProcessId, OccurrenceId = linkedOccurrence.Id })
                .FirstOrDefault();
            if (linkedContext is not null)
                return (linkedContext.ProcessId, linkedContext.OccurrenceId);
        }

        var normalizedTaxId = DomainText.NormalizeTaxId(input.TaxId);
        if (string.IsNullOrWhiteSpace(normalizedTaxId))
            return (null, null);

        var candidates = database.Processes
            .Include(process => process.Occurrences)
            .Where(process => process.TaxId == normalizedTaxId)
            .ToList();
        if (candidates.Count != 1)
            return (null, null);

        var process = candidates[0];
        var occurrence = process.Occurrences
            .OrderByDescending(item => item.Status == OccurrenceStatus.Active)
            .ThenByDescending(item => item.Number)
            .FirstOrDefault();
        return (process.Id, occurrence?.Id);
    }

    private EmailProcessingResult CreateCustomBoardCard(OperationDefinition operation, EmailInput input)
    {
        var initialColumn = operation.Columns.OrderBy(column => column.SortOrder).FirstOrDefault();
        if (initialColumn is null)
        {
            database.IncomingEmails.Add(IncomingEmailFactory.Create(input, null));
            database.SaveChanges();
            return new EmailProcessingResult(false, false, null, $"O quadro {operation.Name} nao possui coluna inicial.");
        }

        var email = IncomingEmailFactory.Create(input, EmailEventType.InitialNotice);
        var occurrence = new ProcessOccurrence
        {
            Number = 1,
            CurrentColumn = initialColumn.Name,
            ReceivedAtUtc = input.ReceivedAtUtc,
            DeadlineAtUtc = CalculateDeadline(operation, input.ReceivedAtUtc),
            History = [new ProcessHistoryEntry
            {
                CreatedAtUtc = DateTime.UtcNow,
                EventType = "EmailCapturado",
                Description = $"Card criado pela regra de e-mail do quadro {operation.Name}."
            }]
        };
        foreach (var definition in operation.CardFields.OrderBy(field => field.SortOrder))
        {
            occurrence.FieldValues.Add(new CardFieldValue
            {
                CardFieldDefinitionId = definition.Id,
                Value = ValueFromEmail(definition, input)
            });
        }

        var primaryValue = operation.CardFields
            .Where(field => field.ShowOnCard)
            .OrderBy(field => field.SortOrder)
            .Select(field => occurrence.FieldValues.First(value => value.CardFieldDefinitionId == field.Id).Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var process = new Process
        {
            OperationDefinitionId = operation.Id,
            TaxId = $"EMAIL-{input.ProviderMessageId}",
            CompanyName = string.IsNullOrWhiteSpace(primaryValue) ? input.Subject : primaryValue,
            Occurrences = [occurrence]
        };
        database.IncomingEmails.Add(email);
        database.Processes.Add(process);
        database.EmailCardLinks.Add(new EmailCardLink
        {
            IncomingEmailId = email.Id,
            ProcessOccurrenceId = occurrence.Id,
            OperationDefinitionId = operation.Id
        });
        database.SaveChanges();
        return new EmailProcessingResult(false, true, null, $"Card criado no quadro {operation.Name}.");
    }

    private static string ValueFromEmail(CardFieldDefinition definition, EmailInput input) => definition.EmailSource switch
    {
        EmailFieldSource.Subject => input.Subject,
        EmailFieldSource.Sender => input.Sender,
        EmailFieldSource.CompanyName => input.CompanyName ?? string.Empty,
        EmailFieldSource.TaxId => input.TaxId ?? string.Empty,
        EmailFieldSource.Competence => input.Competence ?? string.Empty,
        EmailFieldSource.ReceivedAt => input.ReceivedAtUtc.ToLocalTime().ToString(
            definition.FieldType == CardFieldType.Date ? "dd/MM/yyyy" : "dd/MM/yyyy HH:mm",
            CultureInfo.GetCultureInfo("pt-BR")),
        EmailFieldSource.Body => input.Body,
        _ => string.Empty
    };

    private static DateTime? CalculateDeadline(OperationDefinition operation, DateTime receivedAtUtc)
    {
        var rule = operation.DeadlineRules.FirstOrDefault(rule => rule.EventType == EmailEventType.InitialNotice);
        if (rule is null)
            return null;
        return DeadlineCalculator.Calculate(receivedAtUtc, rule.Unit, rule.Amount);
    }
}
