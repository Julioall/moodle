using System.Globalization;
using System.Text;

namespace Moodle.Application.Grading;

/// <summary>
/// Normaliza feedback apenas para detectar reutilização acidental entre alunos.
/// O texto original nunca é alterado por esta classe.
/// </summary>
public static class GradingFeedbackQuality
{
    public static string NormalizeForComparison(string? feedback)
    {
        if (string.IsNullOrWhiteSpace(feedback))
        {
            return string.Empty;
        }

        var decomposed = feedback.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
