using AutoCronos.Desktop.Domain;

namespace AutoCronos.Desktop.Application;

internal static class SuiteTicketApprovalFactory
{
    public static ApprovalItem Create(
        OperationDefinition operation,
        Process process,
        ProcessOccurrence occurrence,
        string? customerTaxId,
        string source,
        string? sourceEmail = null)
    {
        if (!operation.CreatesSuiteTickets)
            throw new InvalidOperationException("Este quadro nao esta configurado para criar chamados.");

        var taxId = DomainText.NormalizeTaxId(customerTaxId) ?? customerTaxId?.Trim();
        var title = string.Concat(operation.Name, " - ", process.CompanyName).Trim();
        var ticketDescription = "Atividade iniciada no quadro " + operation.Name + ".\n" +
                                "Empresa: " + process.CompanyName + "\n" +
                                "CPF/CNPJ: " + (taxId ?? "nao informado") + "\n" +
                                "Competencia: " + (occurrence.Competence ?? "nao informada") + "\n" +
                                "Origem: " + source;

        return new ApprovalItem
        {
            Type = ApprovalType.CreateSuiteTicket,
            ProcessId = process.Id,
            ProcessOccurrenceId = occurrence.Id,
            Title = "Criar chamado no Suite360",
            Description = $"A atividade do quadro {operation.Name} entrou em andamento. Revise os dados antes de enviar.",
            CreatedAtUtc = DateTime.UtcNow,
            SuiteTicketCustomerTaxId = taxId,
            SuiteTicketSourceEmail = sourceEmail?.Trim(),
            SuiteTicketTitle = title,
            SuiteTicketDescription = ticketDescription
        };
    }
}
