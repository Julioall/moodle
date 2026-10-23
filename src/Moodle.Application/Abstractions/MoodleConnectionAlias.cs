using System.Globalization;
using System.Text;

namespace Moodle.Application.Abstractions;

public static class MoodleConnectionAlias
{
    public static string? Normalize(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        var decomposed = alias.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        var normalized = builder.ToString().Normalize(NormalizationForm.FormC);
        return normalized.Length > 64 ? normalized[..64] : normalized;
    }

    public static string NormalizeOrDefault(string? alias) =>
        Normalize(alias) ?? "default";
}
