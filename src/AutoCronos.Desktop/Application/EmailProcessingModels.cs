using AutoCronos.Desktop.Domain;

namespace AutoCronos.Desktop.Application;

public sealed record EmailInput(
    string ProviderMessageId,
    string Subject,
    string? TaxId,
    string? CompanyName,
    string? Competence,
    DateTime ReceivedAtUtc,
    string Sender,
    string Body,
    bool IsReply,
    bool IsFromConnectedAccount,
    IReadOnlyList<string> ConversationMessageIds);

public sealed record EmailProcessingResult(bool AlreadyProcessed, bool CreatedProcess, Guid? ApprovalId, string Message);

internal static class IncomingEmailFactory
{
    public static IncomingEmail Create(EmailInput input, EmailEventType? eventType) => new()
    {
        ProviderMessageId = input.ProviderMessageId,
        Subject = input.Subject,
        TaxId = DomainText.NormalizeTaxId(input.TaxId),
        CompanyName = input.CompanyName?.Trim(),
        Competence = input.Competence,
        Sender = input.Sender.Trim(),
        DetectedEventType = eventType,
        ReceivedAtUtc = input.ReceivedAtUtc,
        IsProcessed = true
    };
}
