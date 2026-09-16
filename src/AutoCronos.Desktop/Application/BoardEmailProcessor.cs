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
            database.IncomingEmails.Add(IncomingEmailFactory.Create(input, null));
            database.SaveChanges();
            return new EmailProcessingResult(false, false, null, "Resposta de e-mail registrada sem criar card.");
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
