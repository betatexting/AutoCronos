using System.Globalization;
using System.Text;

namespace AutoCronos.Desktop.Domain;

public static class DomainText
{
    public static string? NormalizeTaxId(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length is 11 or 14 ? digits : null;
    }

    public static string NormalizeSubject(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToUpperInvariant(character));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

public static class DeadlineCalculator
{
    public static DateTime Calculate(DateTime initial, DeadlineUnit unit, int amount) => unit switch
    {
        DeadlineUnit.Hours => initial.AddHours(amount),
        DeadlineUnit.CalendarDays => initial.AddDays(amount),
        DeadlineUnit.BusinessDays => AddBusinessDays(initial, amount),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unidade de prazo nao reconhecida.")
    };

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
}
