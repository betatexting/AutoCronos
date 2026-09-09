using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AutoCronos.Desktop.Application;

public sealed class ApprovalDecisionService(AutoCronosDbContext database)
{
    public void Resolve(Guid approvalId, bool approve)
    {
        var approval = database.Approvals.Single(x => x.Id == approvalId && x.Status == ApprovalStatus.Pending);
        var email = approval.IncomingEmailId is null ? null : database.IncomingEmails.Single(x => x.Id == approval.IncomingEmailId);
        var process = approval.ProcessId is null ? null : database.Processes.Include(x => x.Occurrences).Single(x => x.Id == approval.ProcessId);

        if (!approve)
        {
            approval.Status = ApprovalStatus.Rejected;
            if (email is not null && approval.Type == ApprovalType.DuplicateNotice) email.IsRejectedAsDuplicate = true;
            database.SaveChanges();
            return;
        }

        if (email is not null && process is not null)
        {
            var active = process.Occurrences.SingleOrDefault(x => x.Status == OccurrenceStatus.Active);
            ProcessOccurrence? linkedOccurrence = null;
            if (approval.Type == ApprovalType.DuplicateNotice && active is not null)
            {
                active.ReceivedAtUtc = email.ReceivedAtUtc;
                database.HistoryEntries.Add(History(active.Id, "InformativoLiberado", "Novo informativo liberado pelo usuario."));
                linkedOccurrence = active;
            }
            else if (approval.Type == ApprovalType.ChangeCompetence && active is not null)
            {
                var previous = active.Competence ?? "nao informada";
                active.Competence = email.Competence;
                database.HistoryEntries.Add(History(active.Id, "CompetenciaAlterada", $"Competencia alterada: {previous} para {email.Competence ?? "nao informada"}."));
                linkedOccurrence = active;
            }
            else if (approval.Type == ApprovalType.ReactivateProcess)
            {
                var initialColumn = database.KanbanColumns
                    .Where(column => column.OperationDefinitionId == process.OperationDefinitionId)
                    .OrderBy(column => column.SortOrder)
                    .Select(column => column.Name)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException("O quadro nao possui uma coluna inicial.");
                linkedOccurrence = new ProcessOccurrence
                {
                    ProcessId = process.Id,
                    Number = process.Occurrences.Count + 1,
                    CurrentColumn = initialColumn,
                    ReceivedAtUtc = email.ReceivedAtUtc,
                    Competence = email.Competence
                };
                database.Occurrences.Add(linkedOccurrence);
                database.HistoryEntries.Add(History(linkedOccurrence.Id, "ProcessoReativado", "Nova ocorrencia criada por reativacao aprovada."));
            }

            if (linkedOccurrence is not null)
                database.EmailCardLinks.Add(new EmailCardLink
                {
                    IncomingEmailId = email.Id,
                    ProcessOccurrenceId = linkedOccurrence.Id,
                    OperationDefinitionId = process.OperationDefinitionId
                });
        }

        approval.Status = ApprovalStatus.Approved;
        database.SaveChanges();
    }

    private static ProcessHistoryEntry History(Guid occurrenceId, string eventType, string description) => new()
    {
        ProcessOccurrenceId = occurrenceId,
        CreatedAtUtc = DateTime.UtcNow,
        EventType = eventType,
        Description = description
    };
}
