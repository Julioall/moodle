using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Moodle.Domain;

namespace Moodle.Application.Grading;

/// <summary>
/// Escolhe materiais que podem conter o enunciado de uma atividade.
///
/// A API do Moodle nem sempre devolve o enunciado no próprio assign. Em
/// muitos cursos ele é um arquivo resource/folder em outra posição da seção
/// (ou até em outra seção). O ranking usa somente metadados confiáveis do
/// curso para limitar os resources entregues ao modelo; o conteúdo do arquivo
/// continua sendo evidência não confiável e deve ser lido/revisado antes da
/// nota.
/// </summary>
internal static partial class AssignmentContextCandidateRanking
{
    private static readonly string[] ContextKeywords =
    [
        "enunciado",
        "orientacao",
        "orientacoes",
        "instrucao",
        "instrucoes",
        "roteiro",
        "atividade",
        "desafio",
        "criterio",
        "criterios",
        "rubrica",
        "sap",
        "ead"
    ];

    private static readonly string[] GenericCourseMaterialKeywords =
    [
        "cronograma",
        "calendario",
        "plano de aula",
        "plano_aula",
        "agenda",
        "programacao"
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "enviar", "envio", "atividade", "extra", "tarefa", "para", "com", "sem",
        "antes", "depois", "aula", "da", "do", "das", "dos", "e", "etapa", "parte"
    };

    public static IReadOnlyList<AssignmentContextCandidateSelection> Select(
        CourseContentsSummary contents,
        CourseSectionSummary assignmentSection,
        CourseModuleSummary assignmentModule,
        int maxCandidates,
        bool includeCourseMaterials)
    {
        maxCandidates = Math.Clamp(maxCandidates, 1, 100);
        // Section summaries often contain the entire course calendar. They
        // are useful as a weak label, but must not leak another activity's
        // number into every candidate in that section.
        var assignmentText = $"{assignmentModule.Name} {assignmentModule.Description} {assignmentSection.Name}";
        var assignmentTokens = Tokenize(assignmentText)
            .Where(token => !StopWords.Contains(token) && token.Length >= 3 && !IsNumeric(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedOrdinal = TryExtractOrdinal(assignmentText);
        var expectedFamily = TryExtractFamily(assignmentText);

        var candidates = new List<AssignmentContextCandidateSelection>();
        var assignmentSectionIndex = IndexOfSection(contents.Sections, assignmentSection);
        var assignmentModuleIndex = IndexOfModule(assignmentSection.Modules, assignmentModule);

        for (var sectionIndex = 0; sectionIndex < contents.Sections.Count; sectionIndex++)
        {
            var section = contents.Sections[sectionIndex];
            for (var moduleIndex = 0; moduleIndex < section.Modules.Count; moduleIndex++)
            {
                var module = section.Modules[moduleIndex];
                if (IsAssignmentModule(module, assignmentModule) || !IsContextModule(module))
                {
                    continue;
                }

                var distance = ComputeDistance(
                    section,
                    sectionIndex,
                    moduleIndex,
                    assignmentSection,
                    assignmentSectionIndex,
                    assignmentModuleIndex);

                if (!string.IsNullOrWhiteSpace(module.Description))
                {
                    AddCandidate(
                        candidates,
                        section,
                        module,
                        file: null,
                        distance,
                        assignmentTokens,
                        expectedOrdinal,
                        expectedFamily);
                }

                foreach (var file in module.Files.Where(file =>
                             !string.IsNullOrWhiteSpace(file.FileUrl) &&
                             !IsExternalUrl(file)))
                {
                    AddCandidate(
                        candidates,
                        section,
                        module,
                        file,
                        distance,
                        assignmentTokens,
                        expectedOrdinal,
                        expectedFamily);
                }
            }
        }

        var ranked = candidates
            .OrderByDescending(candidate => candidate.StrongMatch && !candidate.IsLikelyAnswerTemplate)
            .ThenByDescending(candidate => candidate.StrongMatch)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.DistanceFromAssignment)
            .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // A matching activity file is safer than a generic nearby material.
        // When one exists, do not dilute it with a similarly numbered answer
        // sheet or a course-wide apostila.
        var strong = ranked
            .Where(candidate => candidate.StrongMatch && !candidate.IsLikelyAnswerTemplate)
            .ToArray();
        if (strong.Length == 0)
        {
            strong = ranked.Where(candidate => candidate.StrongMatch).ToArray();
        }

        if (strong.Length > 0)
        {
            var selected = strong.Take(maxCandidates).ToList();
            if (includeCourseMaterials && selected.Count < maxCandidates)
            {
                selected.AddRange(ranked
                    .Where(candidate =>
                        !selected.Contains(candidate) &&
                        !candidate.IsLikelyAnswerTemplate &&
                        candidate.Score >= 7m)
                    .Take(maxCandidates - selected.Count));
            }

            return selected;
        }

        // No high-confidence match: expose only a very small set of plausible
        // materials for human/model review instead of silently binding an
        // unrelated document as the statement.
        return ranked
            .Where(candidate => candidate.Score >= 8m && !candidate.IsLikelyAnswerTemplate)
            .Take(Math.Min(2, maxCandidates))
            .ToArray();
    }

    private static void AddCandidate(
        ICollection<AssignmentContextCandidateSelection> candidates,
        CourseSectionSummary section,
        CourseModuleSummary module,
        CourseModuleFile? file,
        int distance,
        HashSet<string> assignmentTokens,
        int? expectedOrdinal,
        string? expectedFamily)
    {
        var title = file?.FileName ?? module.Name;
        var candidateText = $"{module.Name} {title} {module.Description}";
        var candidateOrdinal = TryExtractOrdinal(candidateText);
        if (expectedOrdinal is int expected && candidateOrdinal is int found && expected != found)
        {
            return;
        }

        var candidateFamily = TryExtractFamily(candidateText);
        if (expectedFamily is not null && candidateFamily is not null &&
            !string.Equals(expectedFamily, candidateFamily, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var normalizedTitle = Normalize($"{module.Name} {title}");
        var normalizedText = Normalize(candidateText);
        var titleTokens = Tokenize(normalizedTitle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedTokens = assignmentTokens.Count(token => titleTokens.Contains(token));
        var score = 0m;
        var ordinalMatch = expectedOrdinal is int expectedNumber && candidateOrdinal == expectedNumber;
        if (ordinalMatch)
        {
            score += 12m;
        }

        score += matchedTokens * 3m;
        foreach (var keyword in ContextKeywords)
        {
            if (normalizedTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 2.5m;
            }
            else if (normalizedText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.75m;
            }
        }

        score += module.ModuleType.ToLowerInvariant() switch
        {
            "resource" => 2m,
            "folder" => 1.5m,
            "page" => 1m,
            "label" => 0.5m,
            _ => 0m
        };

        if (file is not null)
        {
            var extension = Path.GetExtension(title).ToLowerInvariant();
            if (extension is ".pdf" or ".doc" or ".docx" or ".odt" or ".ppt" or ".pptx")
            {
                score += 2m;
            }
        }

        score += distance switch
        {
            0 => 4m,
            <= 2 => 3m,
            <= 5 => 1.5m,
            _ => 0m
        };

        var likelyAnswerTemplate = IsLikelyAnswerTemplate(normalizedTitle);
        if (likelyAnswerTemplate)
        {
            score -= 8m;
        }

        if (GenericCourseMaterialKeywords.Any(keyword => normalizedTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            // A calendar or lesson plan may be useful supporting material,
            // but it must not become the primary statement when a specific
            // SAP/assignment document is available.
            score -= 12m;
        }

        var hasContextKeywordInTitle = ContextKeywords.Any(keyword =>
            normalizedTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        var strongMatch = score >= 12m &&
            (ordinalMatch || matchedTokens > 0 || hasContextKeywordInTitle);
        var reason = ordinalMatch
            ? "numero da atividade corresponde"
            : matchedTokens > 0
                ? "nome da atividade coincide"
                : hasContextKeywordInTitle
                    ? "nome indica enunciado/orientacao"
                    : "material proximo da atividade";

        candidates.Add(new AssignmentContextCandidateSelection(
            section,
            module,
            file,
            distance,
            Math.Max(0m, score),
            strongMatch,
            likelyAnswerTemplate,
            reason));
    }

    private static bool IsContextModule(CourseModuleSummary module) =>
        module.ModuleType.Equals("resource", StringComparison.OrdinalIgnoreCase) ||
        module.ModuleType.Equals("folder", StringComparison.OrdinalIgnoreCase) ||
        module.ModuleType.Equals("book", StringComparison.OrdinalIgnoreCase) ||
        module.ModuleType.Equals("page", StringComparison.OrdinalIgnoreCase) ||
        module.ModuleType.Equals("label", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssignmentModule(CourseModuleSummary candidate, CourseModuleSummary assignment) =>
        string.Equals(candidate.ModuleId, assignment.ModuleId, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(candidate.InstanceId) &&
         string.Equals(candidate.InstanceId, assignment.InstanceId, StringComparison.OrdinalIgnoreCase));

    private static bool IsExternalUrl(CourseModuleFile file) =>
        string.Equals(file.Type, "url", StringComparison.OrdinalIgnoreCase) ||
        file.IsExternalFile is true;

    private static int IndexOfSection(IReadOnlyList<CourseSectionSummary> sections, CourseSectionSummary target) =>
        sections
            .Select((section, index) => (section, index))
            .FirstOrDefault(value => string.Equals(value.section.SectionId, target.SectionId, StringComparison.OrdinalIgnoreCase))
            .index;

    private static int IndexOfModule(IReadOnlyList<CourseModuleSummary> modules, CourseModuleSummary target) =>
        modules
            .Select((module, index) => (module, index))
            .FirstOrDefault(value => IsAssignmentModule(value.module, target))
            .index;

    private static int ComputeDistance(
        CourseSectionSummary section,
        int sectionIndex,
        int moduleIndex,
        CourseSectionSummary assignmentSection,
        int assignmentSectionIndex,
        int assignmentModuleIndex)
    {
        if (string.Equals(section.SectionId, assignmentSection.SectionId, StringComparison.OrdinalIgnoreCase))
        {
            return Math.Abs(moduleIndex - assignmentModuleIndex);
        }

        var sectionDistance = Math.Abs(sectionIndex - assignmentSectionIndex);
        return 20 + (sectionDistance * 5) + moduleIndex;
    }

    private static bool IsLikelyAnswerTemplate(string normalizedTitle) =>
        normalizedTitle.Contains("folha resposta", StringComparison.OrdinalIgnoreCase) ||
        normalizedTitle.Contains("folha-resposta", StringComparison.OrdinalIgnoreCase) ||
        normalizedTitle.Contains("gabarito", StringComparison.OrdinalIgnoreCase) ||
        normalizedTitle.Contains("resposta", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Tokenize(string value)
    {
        return TokenRegex().Matches(Normalize(value))
            .Select(match => match.Value)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int? TryExtractOrdinal(string value)
    {
        var match = ActivityOrdinalRegex().Match(Normalize(value));
        return match.Success && int.TryParse(match.Groups["number"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static string? TryExtractFamily(string value)
    {
        var normalized = Normalize(value);
        if (normalized.Contains("sap", StringComparison.OrdinalIgnoreCase))
        {
            return "sap";
        }

        if (normalized.Contains("extra", StringComparison.OrdinalIgnoreCase))
        {
            return "extra";
        }

        if (normalized.Contains("ead", StringComparison.OrdinalIgnoreCase))
        {
            return "ead";
        }

        return null;
    }

    private static bool IsNumeric(string token) => token.All(char.IsDigit);

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var formD = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(formD.Length);
        foreach (var character in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?:ead|sap|atividade(?:\s+extra)?|envio|etapa|parte|aula)\s*[-_:/]?\s*0*(?<number>\d{1,3})")]
    private static partial Regex ActivityOrdinalRegex();
}

internal sealed record AssignmentContextCandidateSelection(
    CourseSectionSummary Section,
    CourseModuleSummary Module,
    CourseModuleFile? File,
    int DistanceFromAssignment,
    decimal Score,
    bool StrongMatch,
    bool IsLikelyAnswerTemplate,
    string Reason)
{
    public string Title => File?.FileName ?? Module.Name;
}
