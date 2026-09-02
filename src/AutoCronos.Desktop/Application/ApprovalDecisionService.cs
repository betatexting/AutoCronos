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
            if (approval.Type == ApprovalType.DuplicateNotice && active is not null)
            {
                active.ReceivedAtUtc = email.ReceivedAtUtc;
                active.History.Add(History("InformativoLiberado", "Novo informativo liberado pelo usuario."));
            }
            else if (approval.Type == ApprovalType.ChangeCompetence && active is not null)
            {
                var previous = active.Competence ?? "nao informada";
                active.Competence = email.Competence;
                active.History.Add(History("CompetenciaAlterada", $"Competencia alterada: {previous} para {email.Competence ?? "nao informada"}."));
            }
            else if (approval.Type == ApprovalType.ReactivateProcess)
            {
                process.Occurrences.Add(new ProcessOccurrence
                {
                    Number = process.Occurrences.Count + 1,
                    CurrentColumn = "Informativo Recebido",
                    ReceivedAtUtc = email.ReceivedAtUtc,
                    Competence = email.Competence,
                    History = [History("ProcessoReativado", "Nova ocorrencia criada por reativacao aprovada.")]
                });
            }
        }

        approval.Status = ApprovalStatus.Approved;
        database.SaveChanges();
    }

    private static ProcessHistoryEntry History(string eventType, string description) => new() { CreatedAtUtc = DateTime.UtcNow, EventType = eventType, Description = description };
}
