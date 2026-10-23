using System.Globalization;
using System.Text;
using Moodle.Domain;

namespace Moodle.Infrastructure;

internal enum ParticipantClassificationKind
{
    Student = 0,
    KnownStaff = 1,
    UncertainFallback = 2,
    Unclassified = 3
}

internal static class ParticipantClassification
{
    private static readonly HashSet<string> StudentRoles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "student", "students", "learner", "learners",
            "estudante", "estudantes", "aluno", "alunos"
        };

    private static readonly HashSet<string> StaffRoles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "teacher", "editingteacher", "noneditingteacher", "manager", "coursecreator",
            "guest", "monitor", "tutor", "professor", "professora",
            "docente", "coordinator", "coordenador", "coordenadora", "administrator",
            "administrador", "administradora", "gestor", "gestora", "visitante"
        };

    private static readonly HashSet<string> AllowedInstitutionalSuffixes =
        new(StringComparer.OrdinalIgnoreCase) { "go" };

    internal static ParticipantClassificationKind Classify(CourseParticipantSummary participant)
    {
        if (participant.Roles.Count == 0)
        {
            return ParticipantClassificationKind.UncertainFallback;
        }

        var roleKinds = participant.Roles.Select(ClassifyRole).ToArray();
        var hasStudent = roleKinds.Contains(ParticipantClassificationKind.Student);
        var hasStaff = roleKinds.Contains(ParticipantClassificationKind.KnownStaff);
        var hasUnknown = roleKinds.Contains(ParticipantClassificationKind.Unclassified);

        if (hasStudent && !hasStaff && !hasUnknown)
        {
            return ParticipantClassificationKind.Student;
        }

        if (!hasStudent && !hasUnknown)
        {
            return ParticipantClassificationKind.KnownStaff;
        }

        return ParticipantClassificationKind.Unclassified;
    }

    private static ParticipantClassificationKind ClassifyRole(CourseParticipantRole role)
    {
        var shortName = Normalize(role.ShortName ?? string.Empty);
        var name = Normalize(role.Name);
        if (MatchesRoleAlias(shortName, StudentRoles) || MatchesRoleAlias(name, StudentRoles))
        {
            return MatchesRoleAlias(shortName, StaffRoles) || MatchesRoleAlias(name, StaffRoles)
                ? ParticipantClassificationKind.Unclassified
                : ParticipantClassificationKind.Student;
        }

        if (MatchesRoleAlias(shortName, StaffRoles) || MatchesRoleAlias(name, StaffRoles))
        {
            return ParticipantClassificationKind.KnownStaff;
        }

        return ParticipantClassificationKind.Unclassified;
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
        }

        return builder.ToString().Trim().Normalize(NormalizationForm.FormC);
    }

    private static bool MatchesRoleAlias(string normalizedRole, HashSet<string> aliases)
    {
        if (aliases.Contains(normalizedRole)) return true;
        var tokens = normalizedRole.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 1 && aliases.Contains(tokens[0]) &&
            tokens.Skip(1).All(AllowedInstitutionalSuffixes.Contains);
    }
}
