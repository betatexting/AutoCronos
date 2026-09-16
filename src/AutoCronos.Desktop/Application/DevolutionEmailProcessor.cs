using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AutoCronos.Desktop.Application;

/// <summary>Applies the configured Devolucoes rules without coupling business decisions to Gmail.</summary>
public sealed class DevolutionEmailProcessor(AutoCronosDbContext database)
{
    public EmailProcessingResult Process(EmailInput input)
    {
        if (database.IncomingEmails.Any(x => x.ProviderMessageId == input.ProviderMessageId))
            return new(true, false, null, "E-mail ja processado.");

        var operation = database.Operations
            .Include(x => x.EmailRules)
            .Include(x => x.DeadlineRules)
            .Include(x => x.Columns)
            .SingleOrDefault(x => x.Name == "Devolucoes" && x.IsActive);
        if (operation is null) return new(false, false, null, "Operacao Devolucoes nao configurada.");

        var eventType = IdentifyEvent(operation.EmailRules, input.Subject);
        var email = IncomingEmailFactory.Create(input, eventType);
        database.IncomingEmails.Add(email);

        if (eventType is null || string.IsNullOrWhiteSpace(email.TaxId))
        {
            database.SaveChanges();
            return new(false, false, null, "E-mail registrado, mas sem regra reconhecida ou CPF/CNPJ valido.");
        }

        var process = database.Processes.Include(x => x.Occurrences)
            .SingleOrDefault(x => x.OperationDefinitionId == operation.Id && x.TaxId == email.TaxId);
        var activeOccurrence = process?.Occurrences.SingleOrDefault(x => x.Status == OccurrenceStatus.Active);

        if (eventType == EmailEventType.InitialNotice && process is null)
        {
            CreateProcess(operation, email);
            database.SaveChanges();
            return new(false, true, null, "Novo processo criado.");
        }

        var approval = CreateApproval(eventType.Value, process, activeOccurrence, email);
        database.Approvals.Add(approval);
        database.SaveChanges();
        return new(false, false, approval.Id, approval.Title);
    }

    private void CreateProcess(OperationDefinition operation, IncomingEmail email)
    {
        var initialColumn = operation.Columns.OrderBy(column => column.SortOrder).FirstOrDefault()
            ?? throw new InvalidOperationException("O quadro Devolucoes nao possui uma coluna inicial.");
        var occurrence = new ProcessOccurrence
        {
            Number = 1,
            CurrentColumn = initialColumn.Name,
            ReceivedAtUtc = email.ReceivedAtUtc,
            Competence = email.Competence,
            DeadlineAtUtc = CalculateDeadline(operation, EmailEventType.InitialNotice, email.ReceivedAtUtc),
            History = [new() { CreatedAtUtc = DateTime.UtcNow, EventType = "InformativoRecebido", Description = "Processo criado pelo informativo inicial." }]
        };
        var process = new Process
        {
            OperationDefinitionId = operation.Id,
            TaxId = email.TaxId!,
            CompanyName = string.IsNullOrWhiteSpace(email.CompanyName) ? email.TaxId! : email.CompanyName,
            Occurrences = [occurrence]
        };
        database.Processes.Add(process);
        database.EmailCardLinks.Add(new EmailCardLink
        {
            IncomingEmailId = email.Id,
            ProcessOccurrenceId = occurrence.Id,
            OperationDefinitionId = operation.Id
        });
    }

    private static ApprovalItem CreateApproval(EmailEventType eventType, Process? process, ProcessOccurrence? activeOccurrence, IncomingEmail email)
    {
        var type = eventType switch
        {
            EmailEventType.InitialNotice when process is null => ApprovalType.MissingProcess,
            EmailEventType.InitialNotice when activeOccurrence is not null => ApprovalType.DuplicateNotice,
            EmailEventType.InitialNotice => ApprovalType.ReactivateProcess,
            EmailEventType.CompetenceChange when process is null => ApprovalType.MissingProcess,
            EmailEventType.CompetenceChange when activeOccurrence is not null => ApprovalType.ChangeCompetence,
            _ => ApprovalType.ReactivateProcess
        };
        var title = type switch
        {
            ApprovalType.DuplicateNotice => "Possivel repeticao",
            ApprovalType.ReactivateProcess => "Reativacao necessaria",
            ApprovalType.ChangeCompetence => "Alteracao de competencia",
            _ => "Processo nao encontrado"
        };
        return new ApprovalItem { Type = type, ProcessId = process?.Id, IncomingEmailId = email.Id, Title = title, Description = DescriptionFor(type, email), CreatedAtUtc = DateTime.UtcNow };
    }

    private static string DescriptionFor(ApprovalType type, IncomingEmail email) => type switch
    {
        ApprovalType.DuplicateNotice => "Ja existe uma ocorrencia em andamento para este CPF/CNPJ. Escolha liberar ou recusar o novo informativo.",
        ApprovalType.ReactivateProcess => "O processo anterior esta concluido. A nova mensagem exige reativacao antes de prosseguir.",
        ApprovalType.ChangeCompetence => $"A competencia informada ({email.Competence ?? "nao identificada"}) aguarda aprovacao.",
        _ => "Nenhum processo correspondente foi encontrado para a alteracao de competencia."
    };

    private static EmailEventType? IdentifyEvent(IEnumerable<EmailRule> rules, string subject)
    {
        var normalizedSubject = DomainText.NormalizeSubject(subject);
        return rules
            .Select(rule => new { Rule = rule, Pattern = DomainText.NormalizeSubject(rule.SubjectPattern) })
            .Where(candidate => normalizedSubject.Contains(candidate.Pattern, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Pattern.Length)
            .FirstOrDefault()
            ?.Rule.EventType;
    }

    private DateTime? CalculateDeadline(OperationDefinition operation, EmailEventType type, DateTime receivedAtUtc)
    {
        var rule = operation.DeadlineRules.SingleOrDefault(x => x.EventType == type);
        if (rule is null) return null;
        return DeadlineCalculator.Calculate(receivedAtUtc, rule.Unit, rule.Amount);
    }
}
