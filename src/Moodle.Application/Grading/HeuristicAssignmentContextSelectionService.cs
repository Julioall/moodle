using System.Globalization;
using System.Text.RegularExpressions;
using Moodle.Application.Abstractions;

namespace Moodle.Application.Grading;

public sealed partial class HeuristicAssignmentContextSelectionService : IAssignmentContextSelectionService
{
    private static readonly string[] ContextKeywords =
    [
        "enunciado",
        "orientacao",
        "orientacoes",
        "atividade",
        "criterio",
        "criterios",
        "rubrica",
        "avaliacao",
        "sap",
        "etapa",
        "envio",
        "situacao",
        "aprendizagem",
        "desafio",
        "instrucao",
        "instrucoes",
        "guia",
        "manual"
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

    public Task<AssignmentContextSelectionResult> SelectAsync(
        AssignmentContextSelectionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Candidates.Count == 0)
        {
            return Task.FromResult(new AssignmentContextSelectionResult(
                SelectedCandidateId: null,
                Classification: "none",
                Confidence: 0m,
                Reason: null,
                SupportingCandidateIds: [],
                Warnings: ["Nenhum documento candidato foi encontrado para o enunciado da tarefa."]));
        }

        var assignmentTokens = Tokenize(request.AssignmentName)
            .Concat(Tokenize(request.AssignmentDescription ?? string.Empty))
            .Where(token => token.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expectedOrdinal = TryExtractActivityOrdinal(request.AssignmentName);
        var candidatesWithOrdinal = request.Candidates
            .Where(candidate => TryExtractCandidateOrdinal(candidate) is not null)
            .ToArray();
        var compatibilityPool = candidatesWithOrdinal.Length > 0
            ? candidatesWithOrdinal
            : request.Candidates;
        var compatibleCandidates = compatibilityPool
            .Where(candidate => IsCompatibleWithActivity(candidate, expectedOrdinal))
            .ToArray();
        if (compatibleCandidates.Length == 0)
        {
            return Task.FromResult(new AssignmentContextSelectionResult(
                SelectedCandidateId: null,
                Classification: "blocked",
                Confidence: 0m,
                Reason: $"Nenhum documento de contexto corresponde ao identificador da atividade {expectedOrdinal:00}.",
                SupportingCandidateIds: [],
                Warnings: ["Os documentos encontrados pertencem a outra atividade; a selecao heuristica foi bloqueada para evitar vincular o enunciado errado."]));
        }

        var ranked = compatibleCandidates
            .Select(candidate => new CandidateScore(candidate, Score(candidate, assignmentTokens, request.AssignmentName)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Candidate.DistanceFromAssignment ?? int.MaxValue)
            .ThenBy(candidate => candidate.Candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selected = ranked[0];
        var confidence = ToConfidence(selected.Score);
        var warnings = confidence < 0.45m
            ? ["Baixa confianca na selecao automatica do enunciado. Validacao humana recomendada."]
            : Array.Empty<string>();

        return Task.FromResult(new AssignmentContextSelectionResult(
            selected.Candidate.CandidateId,
            confidence >= 0.45m ? "assignment_statement" : "possible_context",
            confidence,
            BuildReason(selected.Candidate, selected.Score),
            ranked.Skip(1)
                .Where(candidate => candidate.Score >= Math.Max(2m, selected.Score - 2m))
                .Take(3)
                .Select(candidate => candidate.Candidate.CandidateId)
                .ToArray(),
            warnings));
    }

    private static decimal Score(
        AssignmentContextCandidate candidate,
        HashSet<string> assignmentTokens,
        string assignmentName)
    {
        var title = Normalize(candidate.Title);
        var text = Normalize(candidate.ExtractedText ?? string.Empty);
        var combined = $"{title} {text}";
        var score = 0m;
        var expectedOrdinal = TryExtractActivityOrdinal(assignmentName);
        var candidateOrdinal = TryExtractCandidateOrdinal(candidate);
        if (expectedOrdinal is int expected && candidateOrdinal is int candidateNumber && expected == candidateNumber)
        {
            score += 12m;
        }

        foreach (var token in assignmentTokens)
        {
            if (title.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += token.Length <= 2 ? 1.5m : 4m;
            }
            else if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += token.Length <= 2 ? 0.5m : 1.5m;
            }
        }

        foreach (var keyword in ContextKeywords)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 2.5m;
            }
            else if (combined.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 1m;
            }
        }

        score += candidate.SourceType.ToLowerInvariant() switch
        {
            "assign" => 4m,
            "resource" => 2m,
            "page" => 1.5m,
            "folder" => 1m,
            "label" => 0.75m,
            _ => 0m
        };

        if (candidate.SourceType.Equals("resource", StringComparison.OrdinalIgnoreCase))
        {
            var ext = System.IO.Path.GetExtension(title).ToLowerInvariant();
            if (ext is ".pdf" or ".docx" or ".doc" or ".pptx")
            {
                score += 2.5m; // Boost para arquivos típicos de enunciado
            }
        }

        if (GenericCourseMaterialKeywords.Any(keyword => title.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            // Keep generic planning documents available as supporting context,
            // but never let them outrank an activity-specific statement.
            score -= 14m;
        }

        if (expectedOrdinal is int expectedSap && candidateOrdinal == expectedSap &&
            title.Contains("sap", StringComparison.OrdinalIgnoreCase))
        {
            score += 8m;
        }

        if (candidate.DistanceFromAssignment is int distance)
        {
            score += Math.Max(0, 3 - distance);
        }

        if (!string.IsNullOrWhiteSpace(candidate.ExtractedText))
        {
            score += 2m; // Maior incentivo para arquivos que possuem texto legivel
        }

        var normalizedAssignmentName = Normalize(assignmentName);
        var isSubPart = normalizedAssignmentName.Contains("etapa") ||
                        normalizedAssignmentName.Contains("parte") ||
                        normalizedAssignmentName.Contains("fase");

        var matchGroup = Regex.Match(normalizedAssignmentName, @"(?:sap|sa|projeto|atividade)\s*\d+");
        if (isSubPart && matchGroup.Success)
        {
            var groupValue = matchGroup.Value;
            if (title.Contains(groupValue) && !title.Contains("etapa") && !title.Contains("parte") && !title.Contains("fase"))
            {
                score += 5m; // Boost overarching document
                if (title.EndsWith(".pdf") || candidate.SourceType == "resource")
                {
                    score += 5m;
                }
            }
        }

        return score;
    }

    private static decimal ToConfidence(decimal score)
    {
        if (score <= 0)
        {
            return 0m;
        }

        return Math.Min(0.95m, Math.Round(score / 20m, 2));
    }

    private static string BuildReason(AssignmentContextCandidate candidate, decimal score)
    {
        return $"Selecionado por similaridade heuristica com a tarefa e palavras-chave de contexto. Fonte: {candidate.SourceType}; score={score.ToString("0.##", CultureInfo.InvariantCulture)}.";
    }

    private static IReadOnlyList<string> Tokenize(string value)
    {
        return TokenRegex().Matches(Normalize(value))
            .Select(match => match.Value)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray();
        return new string(chars).Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }

    private sealed record CandidateScore(AssignmentContextCandidate Candidate, decimal Score);

    private static bool IsCompatibleWithActivity(AssignmentContextCandidate candidate, int? expectedOrdinal)
    {
        if (expectedOrdinal is not int expected)
        {
            return true;
        }

        var candidateOrdinal = TryExtractCandidateOrdinal(candidate);
        return candidateOrdinal is null || candidateOrdinal == expected;
    }

    private static int? TryExtractActivityOrdinal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = ActivityOrdinalRegex().Match(Normalize(value));
        return match.Success && int.TryParse(match.Groups["number"].Value, out var number)
            ? number
            : null;
    }

    private static int? TryExtractCandidateOrdinal(AssignmentContextCandidate candidate)
    {
        var activityOrdinal = TryExtractActivityOrdinal(candidate.Title);
        if (activityOrdinal is not null)
        {
            return activityOrdinal;
        }

        var title = Normalize(candidate.Title);
        var prefixMatch = CandidateOrdinalPrefixRegex().Match(title);
        return prefixMatch.Success && int.TryParse(prefixMatch.Groups["number"].Value, out var number)
            ? number
            : null;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?:ead|atividade|sap|envio)\s*[-_:/]?\s*0*(?<number>\d{1,3})")]
    private static partial Regex ActivityOrdinalRegex();

    [GeneratedRegex(@"^0*(?<number>\d{1,3})(?=[_\-\s])")]
    private static partial Regex CandidateOrdinalPrefixRegex();
}
