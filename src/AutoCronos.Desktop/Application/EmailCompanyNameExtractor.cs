using System.Text.RegularExpressions;

namespace AutoCronos.Desktop.Application;

public static class EmailCompanyNameExtractor
{
    private const string TaxIdPattern = @"(?<!\d)(?:\d{2}\.?\d{3}\.?\d{3}[\/-]?\d{4}-?\d{2})(?!\d)";

    public static string? Extract(string subject, string body) =>
        ExtractCompanyNameBeforeTaxId(subject) ?? ExtractFromBody(body);

    public static string? ExtractCompanyNameBeforeTaxId(string value)
    {
        var taxIdMatch = Regex.Match(value, TaxIdPattern);
        if (!taxIdMatch.Success)
            return null;

        var textBeforeTaxId = value[..taxIdMatch.Index].Trim().TrimEnd('-', '\u2013', '\u2014', ' ');
        var separator = Regex.Match(textBeforeTaxId, @"\s*[-\u2013\u2014]\s*");
        var companyName = separator.Success
            ? textBeforeTaxId[(separator.Index + separator.Length)..].Trim()
            : textBeforeTaxId;
        return string.IsNullOrWhiteSpace(companyName) ? null : companyName;
    }

    private static string? ExtractFromBody(string body)
    {
        foreach (var pattern in new[]
                 {
                     @"(?:razao social|raz.o social)\s*[:\-]\s*(?<value>[^\r\n]+)",
                     @"(?:nome empresarial|nome fantasia)\s*[:\-]\s*(?<value>[^\r\n]+)"
                 })
        {
            var match = Regex.Match(body, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            var value = match.Groups["value"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}
