using System.Globalization;
using System.Text;
using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AutoCronos.Desktop.Application;

public sealed record EmailInput(string ProviderMessageId, string Subject, string? TaxId, string? CompanyName, string? Competence, DateTime ReceivedAtUtc);
public sealed record EmailProcessingResult(bool AlreadyProcessed, bool CreatedProcess, Guid? ApprovalId, string Message);

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
            .SingleOrDefault(x => x.Name == "Devolucoes" && x.IsActive);
        if (operation is null) return new(false, false, null, "Operacao Devolucoes nao configurada.");

        var eventType = IdentifyEvent(operation.EmailRules, input.Subject);
        var email = new IncomingEmail
        {
            ProviderMessageId = input.ProviderMessageId,
            Subject = input.Subject,
            TaxId = NormalizeTaxId(input.TaxId),
            CompanyName = input.CompanyName?.Trim(),
            Competence = input.Competence,
            DetectedEventType = eventType,
            ReceivedAtUtc = input.ReceivedAtUtc,
            IsProcessed = true
        };
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
        var occurrence = new ProcessOccurrence
        {
            Number = 1,
            CurrentColumn = "Informativo Recebido",
            ReceivedAtUtc = email.ReceivedAtUtc,
            Competence = email.Competence,
            DeadlineAtUtc = CalculateDeadline(operation, EmailEventType.InitialNotice, email.ReceivedAtUtc),
            History = [new() { CreatedAtUtc = DateTime.UtcNow, EventType = "InformativoRecebido", Description = "Processo criado pelo informativo inicial." }]
        };
        database.Processes.Add(new Process
        {
            OperationDefinitionId = operation.Id,
            TaxId = email.TaxId!,
            CompanyName = string.IsNullOrWhiteSpace(email.CompanyName) ? email.TaxId! : email.CompanyName,
            Occurrences = [occurrence]
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
        var normalizedSubject = Normalize(subject);
        return rules.FirstOrDefault(rule => normalizedSubject.Contains(Normalize(rule.SubjectPattern), StringComparison.Ordinal))?.EventType;
    }

    private DateTime? CalculateDeadline(OperationDefinition operation, EmailEventType type, DateTime receivedAtUtc)
    {
        var rule = operation.DeadlineRules.SingleOrDefault(x => x.EventType == type);
        if (rule is null) return null;
        return rule.Unit switch
        {
            DeadlineUnit.Hours => receivedAtUtc.AddHours(rule.Amount),
            DeadlineUnit.CalendarDays => receivedAtUtc.AddDays(rule.Amount),
            DeadlineUnit.BusinessDays => AddBusinessDays(receivedAtUtc, rule.Amount),
            _ => null
        };
    }

    private static DateTime AddBusinessDays(DateTime initial, int days)
    {
        var result = initial;
        while (days > 0)
        {
            result = result.AddDays(1);
            if (result.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday) days--;
        }
        return result;
    }

    private static string? NormalizeTaxId(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length is 11 or 14 ? digits : null;
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) builder.Append(character);
        return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }
}
