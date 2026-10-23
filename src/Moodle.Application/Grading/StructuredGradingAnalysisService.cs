using Moodle.Application.Abstractions;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Moodle.Application.Grading;

/// <summary>
/// Implementacao de analise assistida baseada em texto.
/// Esta versao gera um rascunho estruturado com base no texto da submissao e nos criterios informados.
/// Nunca bloqueia quando ha conteudo legivel — gera rascunho com confianca proporcional a qualidade dos criterios.
/// Para producao, substitua por implementacao que integre com servico de IA/LLM.
/// </summary>
public sealed partial class StructuredGradingAnalysisService : IGradingAnalysisService
{
    public Task<GradingAnalysisResult> AnalyzeAsync(
        GradingAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SubmissionText))
        {
            return Task.FromResult(new GradingAnalysisResult(
                SuggestedGrade: null,
                Confidence: 0m,
                AnalysisStatus.BlockedEmptySubmission,
                FeedbackToStudent: null,
                PrivateNotesToTeacher: "Submissao sem texto legivel. Verifique se o arquivo foi baixado e extraido corretamente.",
                CriterionAnalysis: [],
                Blocks: ["Submissao sem conteudo textual para analise."]));
        }

        // --- Resolucao de criterios efetivos (cascata) — apenas para diagnostico ---
        var criteriaSource = ResolveCriteria(request.RubricOrCriteria, request.ActivityDescription, request.TeacherInstructions);
        var effectiveCriteria = criteriaSource.Text;
        var hasApproximateCriteria = criteriaSource.Source == CriteriaSourceKind.Approximate;

        // --- Resolucao de MaxGrade efetivo (cascata) — apenas para diagnostico ---
        var effectiveMaxGrade = request.MaxGrade > 0
            ? request.MaxGrade
            : TryExtractMaxGrade(request.RubricOrCriteria) ?? TryExtractMaxGrade(request.ActivityDescription) ?? 0m;
        var hasMaxGrade = effectiveMaxGrade > 0;

        var wordCount = CountWords(request.SubmissionText);

        // --- Pre-validacao aprovada: item pronto para analise pela IA ---
        // Nao gera nota sugerida, feedback para o aluno nem criterios avaliados.
        // Essas saidas serao geradas exclusivamente pelo fluxo de IA.
        var diagnosticNotes = BuildDiagnosticNotes(
            wordCount, criteriaSource.Source, hasMaxGrade, effectiveMaxGrade, effectiveCriteria);
        if (hasApproximateCriteria && CountWords(effectiveCriteria!) < 15)
        {
            diagnosticNotes += " O enunciado é curto e será usado como contexto aproximado; a IA deve confirmar os critérios antes de propor a nota.";
        }

        return Task.FromResult(new GradingAnalysisResult(
            SuggestedGrade: null,
            Confidence: 0m,
            AnalysisStatus.AwaitingAiAnalysis,
            FeedbackToStudent: null,
            PrivateNotesToTeacher: diagnosticNotes,
            CriterionAnalysis: [],
            Blocks: []));
    }

    private static CriteriaResolution ResolveCriteria(string? rubricOrCriteria, string? activityDescription, string? teacherInstructions)
    {
        var hasTeacherInstructions = !string.IsNullOrWhiteSpace(teacherInstructions);
        var criteriaText = rubricOrCriteria;

        if (!string.IsNullOrWhiteSpace(criteriaText))
        {
            if (hasTeacherInstructions)
            {
                return new CriteriaResolution($"{criteriaText}\n{teacherInstructions}", CriteriaSourceKind.TeacherOverride);
            }
            return new CriteriaResolution(criteriaText, CriteriaSourceKind.Formal);
        }

        if (!string.IsNullOrWhiteSpace(activityDescription))
        {
            if (hasTeacherInstructions)
            {
                return new CriteriaResolution($"{activityDescription}\n{teacherInstructions}", CriteriaSourceKind.TeacherOverride);
            }
            return new CriteriaResolution(activityDescription, CriteriaSourceKind.Approximate);
        }

        if (hasTeacherInstructions)
        {
            return new CriteriaResolution(teacherInstructions, CriteriaSourceKind.TeacherOverride);
        }

        return new CriteriaResolution(null, CriteriaSourceKind.None);
    }

    /// <summary>
    /// Constroi notas diagnosticas sobre a viabilidade da correcao,
    /// sem gerar nota, feedback ou criterios avaliativos.
    /// Estas notas ficam visiveis ao professor como PrivateNotesToTeacher.
    /// </summary>
    private static string BuildDiagnosticNotes(
        int wordCount,
        CriteriaSourceKind criteriaSource,
        bool hasMaxGrade,
        decimal effectiveMaxGrade,
        string? effectiveCriteria)
    {
        var notes = new System.Text.StringBuilder();

        notes.Append($"Pre-validacao concluida. Texto da submissao: {wordCount} palavras. ");

        switch (criteriaSource)
        {
            case CriteriaSourceKind.Formal:
                notes.Append("Criterios formais (rubrica) identificados. ");
                break;
            case CriteriaSourceKind.TeacherOverride:
                notes.Append("Instrucoes do professor identificadas. ");
                break;
            case CriteriaSourceKind.Approximate:
                notes.Append("Enunciado da atividade disponivel como contexto pedagogico. ");
                break;
            case CriteriaSourceKind.None:
                notes.Append("Nenhum criterio, rubrica ou enunciado identificado. ");
                break;
        }

        if (hasMaxGrade)
        {
            notes.Append($"Nota maxima: {effectiveMaxGrade} pontos. ");
        }
        else
        {
            notes.Append("Nota maxima nao identificada. ");
        }

        notes.Append("Item pronto para analise via IA. Use prepare_ai_grading_batch para gerar nota e feedback.");
        return notes.ToString();
    }

    [Obsolete("Metodo legado. Uso exclusivo para diagnostico tecnico. A geracao de criterios avaliados e feita pela IA.")]
    private static IReadOnlyList<GradingCriterionAnalysis> BuildCriterionAnalysis(
        IReadOnlyList<string> criteria,
        string submissionText,
        decimal maxGrade)
    {
        if (criteria.Count == 0)
        {
            return [];
        }

        var results = new List<GradingCriterionAnalysis>();
        var submissionLower = submissionText.ToLowerInvariant();

        // Distribuir pontos com residual no ultimo criterio
        var pointsPerCriterion = Math.Round(maxGrade / criteria.Count, 2);
        var accumulatedPoints = 0m;

        for (var i = 0; i < criteria.Count; i++)
        {
            var criterion = criteria[i].Trim();
            var criterionWords = ExtractSignificantWords(criterion);

            var matchedWords = criterionWords.Where(word => submissionLower.Contains(word)).ToArray();
            var coverage = criterionWords.Length > 0
                ? (decimal)matchedWords.Length / criterionWords.Length
                : 0m;

            // Ultimo criterio recebe o residual
            var currentMax = (i == criteria.Count - 1 && maxGrade > 0)
                ? maxGrade - accumulatedPoints
                : pointsPerCriterion;
            accumulatedPoints += currentMax;

            var suggestedPoints = Math.Round(currentMax * Math.Min(coverage * 1.5m, 1m), 2);
            var needsReview = coverage < 0.5m;

            // Evidence com palavras-chave concretas da entrega
            var evidence = matchedWords.Length > 0
                ? BuildConcreteEvidence(criterion, matchedWords)
                : "Nao foram identificados elementos diretamente relacionados a este aspecto no texto analisado.";

            // Gaps com orientacao especifica
            var gaps = needsReview
                ? BuildConcreteGaps(criterion, criterionWords, matchedWords)
                : null;

            results.Add(new GradingCriterionAnalysis(
                CriterionId: $"C{i + 1}",
                CriterionText: criterion,
                MaxPoints: currentMax,
                SuggestedPoints: suggestedPoints,
                EvidenceFound: evidence,
                Gaps: gaps,
                TeacherReviewRequired: needsReview));
        }

        return results;
    }

    private static IReadOnlyList<GradingCriterionAnalysis> BuildCriterionAnalysisWithoutGrade(
        IReadOnlyList<string> criteria,
        string submissionText)
    {
        if (criteria.Count == 0)
        {
            return [];
        }

        var results = new List<GradingCriterionAnalysis>();
        var submissionLower = submissionText.ToLowerInvariant();

        for (var i = 0; i < criteria.Count; i++)
        {
            var criterion = criteria[i].Trim();
            var criterionWords = ExtractSignificantWords(criterion);

            var matchedWords = criterionWords.Where(word => submissionLower.Contains(word)).ToArray();
            var coverage = criterionWords.Length > 0
                ? (decimal)matchedWords.Length / criterionWords.Length
                : 0m;

            var needsReview = coverage < 0.5m;

            results.Add(new GradingCriterionAnalysis(
                CriterionId: $"C{i + 1}",
                CriterionText: criterion,
                MaxPoints: null,
                SuggestedPoints: null,
                EvidenceFound: matchedWords.Length > 0
                    ? BuildConcreteEvidence(criterion, matchedWords)
                    : "Nao foram identificados elementos diretamente relacionados a este aspecto no texto analisado.",
                Gaps: needsReview
                    ? BuildConcreteGaps(criterion, criterionWords, matchedWords)
                    : null,
                TeacherReviewRequired: needsReview));
        }

        return results;
    }

    /// <summary>
    /// Feedback genérico humanizado quando não há critérios suficientes para análise detalhada.
    /// </summary>
    private static string BuildGenericHumanizedFeedback(string assignmentName)
    {
        var cleanName = CleanAssignmentName(assignmentName);
        return $"Olá.\n\n" +
               $"Sua entrega para a atividade '{cleanName}' foi recebida. " +
               "No momento, não foi possível realizar uma avaliação detalhada por critérios. " +
               "Aguarde o retorno do professor/tutor com o feedback completo.";
    }

    /// <summary>
    /// Gera feedback pedagógico humanizado a partir dos dados estruturados da análise.
    /// Este método é a "segunda etapa de síntese" — transforma evidências técnicas,
    /// gaps e notas por critério em texto natural adequado para envio direto ao aluno.
    ///
    /// Regras seguidas:
    /// 1. Escrever diretamente para o aluno, em tom respeitoso e acolhedor.
    /// 2. Linguagem natural de professor/tutor.
    /// 3. Sem frases repetitivas tipo "o texto aborda elementos relacionados".
    /// 4. Sem palavras-chave soltas entre parênteses.
    /// 5. Sem IDs, hashes ou detalhes internos.
    /// 6. Sem mencionar análise automática.
    /// 7. Sem inventar informações ausentes na entrega.
    /// 8. Tom respeitoso mesmo para entregas fracas.
    /// 9. Justificativa resumida da nota.
    /// 10. Texto corrido com tópicos pontuais quando melhora a leitura.
    /// 11. Nota sugerida no final.
    /// 12. Português do Brasil.
    /// 13. Sem tom punitivo.
    /// 14. Sem elogios genéricos quando nota for baixa.
    /// </summary>
    private static string GenerateStudentFriendlyFeedback(
        string assignmentName,
        IReadOnlyList<GradingCriterionAnalysis> criteria,
        decimal? suggestedGrade,
        decimal maxGrade,
        string submissionText)
    {
        var sb = new System.Text.StringBuilder();
        var cleanName = CleanAssignmentName(assignmentName);

        var strongCriteria = criteria.Where(c => (c.SuggestedPoints ?? 0) >= (c.MaxPoints ?? 0) * 0.5m).ToList();
        var weakCriteria = criteria.Where(c => c.TeacherReviewRequired).ToList();
        var gradeRatio = (suggestedGrade.HasValue && maxGrade > 0) ? suggestedGrade.Value / maxGrade : 0m;

        // --- Saudação ---
        sb.AppendLine("Olá.");
        sb.AppendLine();

        // --- Parágrafo de abertura — contextualizado pela nota e atividade ---
        sb.Append(BuildOpeningParagraph(cleanName, gradeRatio, strongCriteria.Count, criteria.Count));
        sb.AppendLine();
        sb.AppendLine();

        // --- Pontos positivos (apenas se houver critérios atendidos) ---
        if (strongCriteria.Count > 0)
        {
            var positivePoints = strongCriteria
                .Select(c => BuildHumanizedPositivePoint(c))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (positivePoints.Count > 0)
            {
                sb.AppendLine("Você demonstrou pontos positivos, como:");
                foreach (var point in positivePoints)
                {
                    sb.AppendLine($"- {LowercaseFirst(point)};");
                }
                sb.AppendLine();
            }
        }

        // --- Pontos de melhoria (apenas se houver lacunas reais) ---
        if (weakCriteria.Count > 0)
        {
            var improvementPoints = weakCriteria
                .Select(c => BuildHumanizedImprovementPoint(c))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (improvementPoints.Count > 0)
            {
                sb.Append(BuildImprovementParagraph(improvementPoints));
                sb.AppendLine();
                sb.AppendLine();
            }
        }

        // --- Parágrafo de fechamento com justificativa da nota ---
        sb.Append(BuildClosingParagraph(gradeRatio, strongCriteria.Count, weakCriteria.Count, criteria.Count));
        sb.AppendLine();

        // --- Nota sugerida no final ---
        if (suggestedGrade.HasValue && maxGrade > 0)
        {
            sb.AppendLine();
            sb.Append($"Nota sugerida: {suggestedGrade.Value:0.#}/{maxGrade:0.#}");
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Parágrafo de abertura do feedback — varia com a qualidade da entrega.
    /// Evita elogios genéricos para notas baixas e tom punitivo.
    /// </summary>
    private static string BuildOpeningParagraph(
        string activityName, decimal gradeRatio, int strongCount, int totalCount)
    {
        if (gradeRatio >= 0.8m)
        {
            return $"Sua atividade apresenta um bom desenvolvimento do tema proposto. " +
                   $"Foi possível identificar que você compreendeu os aspectos centrais solicitados na atividade.";
        }

        if (gradeRatio >= 0.5m)
        {
            return $"Sua atividade aborda o tema proposto e apresenta elementos relevantes. " +
                   $"Alguns aspectos foram bem desenvolvidos, mas há pontos que podem ser aprimorados.";
        }

        if (strongCount > 0)
        {
            return $"Sua atividade demonstra esforço na abordagem do tema proposto. " +
                   $"Alguns pontos foram contemplados, porém há aspectos importantes que precisam de maior aprofundamento.";
        }

        return $"Sua atividade foi recebida e analisada. " +
               $"Alguns dos aspectos solicitados precisam de maior desenvolvimento para atender aos critérios da atividade.";
    }

    /// <summary>
    /// Transforma um critério atendido em ponto positivo humanizado.
    /// Usa a descrição do critério como base, NÃO as palavras-chave brutas.
    /// </summary>
    private static string BuildHumanizedPositivePoint(GradingCriterionAnalysis criterion)
    {
        var criterionDesc = ConsolidateCriterionDescription(criterion.CriterionText);

        // Transformar a descrição do critério em ponto positivo natural
        // Ex: "Elaborar um plano de gerenciamento" → "elaboração de um plano de gerenciamento"
        //     "Identificar riscos físicos" → "identificação de riscos físicos"
        var lower = criterionDesc.ToLowerInvariant();

        // Se o critério começa com verbo, nominalizar para soar natural como ponto positivo
        var nominalized = TryNominalizeVerb(criterionDesc);
        if (nominalized != null)
        {
            return nominalized;
        }

        // Fallback: usar a descrição diretamente, prefixada com "abordagem sobre"
        return $"abordagem sobre {LowercaseFirst(criterionDesc)}";
    }

    /// <summary>
    /// Transforma um critério com lacuna em ponto de melhoria humanizado.
    /// Gera orientação específica baseada no critério, sem palavras-chave soltas.
    /// </summary>
    private static string BuildHumanizedImprovementPoint(GradingCriterionAnalysis criterion)
    {
        var criterionDesc = ConsolidateCriterionDescription(criterion.CriterionText);
        var lower = criterionDesc.ToLowerInvariant();

        // Gerar sugestão de melhoria baseada no verbo do critério
        if (lower.StartsWith("elaborar") || lower.StartsWith("criar") || lower.StartsWith("produzir"))
        {
            return $"desenvolva com mais detalhamento a parte referente a {RemoveLeadingVerb(criterionDesc)}";
        }

        if (lower.StartsWith("identificar") || lower.StartsWith("indicar") || lower.StartsWith("listar"))
        {
            return $"identifique de forma mais clara e específica os elementos referentes a {RemoveLeadingVerb(criterionDesc)}";
        }

        if (lower.StartsWith("descrever") || lower.StartsWith("explicar") || lower.StartsWith("apresentar"))
        {
            return $"apresente com mais profundidade os aspectos referentes a {RemoveLeadingVerb(criterionDesc)}";
        }

        if (lower.StartsWith("relacionar") || lower.StartsWith("comparar") || lower.StartsWith("analisar"))
        {
            return $"aprofunde a análise e a relação entre os elementos referentes a {RemoveLeadingVerb(criterionDesc)}";
        }

        if (lower.StartsWith("adequar") || lower.StartsWith("organizar") || lower.StartsWith("estruturar"))
        {
            return $"revise a organização e adequação referente a {RemoveLeadingVerb(criterionDesc)}";
        }

        // Fallback genérico mas contextualizado
        return $"aprofunde os aspectos referentes a {LowercaseFirst(criterionDesc)}";
    }

    /// <summary>
    /// Constrói parágrafo de melhorias em texto corrido, não como lista mecânica.
    /// </summary>
    private static string BuildImprovementParagraph(IReadOnlyList<string> improvements)
    {
        if (improvements.Count == 1)
        {
            return $"Para melhorar sua entrega, {improvements[0]}.";
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("Para melhorar sua entrega, considere os seguintes pontos: ");
        sb.Append(LowercaseFirst(improvements[0]));
        for (var i = 1; i < improvements.Count; i++)
        {
            sb.Append(i == improvements.Count - 1 ? "; e " : "; ");
            sb.Append(LowercaseFirst(improvements[i]));
        }
        sb.Append('.');
        return sb.ToString();
    }

    /// <summary>
    /// Parágrafo de fechamento com justificativa resumida da nota.
    /// </summary>
    private static string BuildClosingParagraph(
        decimal gradeRatio, int strongCount, int weakCount, int totalCount)
    {
        if (gradeRatio >= 0.8m)
        {
            return "De forma geral, sua entrega atende aos critérios da atividade de maneira satisfatória.";
        }

        if (gradeRatio >= 0.5m)
        {
            return "De forma geral, sua entrega atende parcialmente aos critérios da atividade, " +
                   "mas ainda pode ser aprimorada em alguns aspectos.";
        }

        if (strongCount > 0)
        {
            return "De forma geral, sua entrega apresenta elementos relevantes, " +
                   "mas precisa ser aprofundada em aspectos centrais dos critérios da atividade.";
        }

        return "De forma geral, sua entrega precisa de maior desenvolvimento " +
               "para atender aos critérios propostos na atividade. Caso tenha dúvidas, " +
               "procure orientação com o professor/tutor.";
    }

    /// <summary>
    /// Consolida critérios "quebrados" (vindos de extração bruta de PDF) em descrições
    /// pedagógicas legíveis. Remove fragmentos soltos, junta partes truncadas.
    /// Ex: "Elaborar, em documento de texto, de um plano de gerenciamento de"
    ///   → "Elaborar um plano de gerenciamento em documento de texto"
    /// </summary>
    private static string ConsolidateCriterionDescription(string rawCriterion)
    {
        var cleaned = rawCriterion.Trim().TrimEnd('.', ',', ';');

        // Remover preposições soltas no final (texto truncado)
        var trailingPrepositions = new[] { " de", " da", " do", " das", " dos", " em", " no", " na", " nos", " nas", " para", " com" };
        foreach (var prep in trailingPrepositions)
        {
            if (cleaned.EndsWith(prep, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[..^prep.Length].TrimEnd(',', ' ');
            }
        }

        // Remover vírgulas desnecessárias antes de preposições ("Elaborar, em documento" → "Elaborar em documento")
        cleaned = Regex.Replace(cleaned, @",\s+(em|de|da|do|no|na|para|com|das|dos|nos|nas)\b", " $1");

        // Remover "de" redundante ("de um plano de" → "um plano de")
        cleaned = Regex.Replace(cleaned, @"\bde\s+de\b", "de", RegexOptions.IgnoreCase);

        // Capitalizar primeira letra
        if (cleaned.Length > 0 && char.IsLower(cleaned[0]))
        {
            cleaned = char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
        }

        return cleaned;
    }

    /// <summary>
    /// Tenta nominalizar o verbo inicial de um critério para usar como ponto positivo.
    /// Ex: "Elaborar um plano" → "elaboração de um plano"
    ///     "Identificar riscos" → "identificação de riscos"
    /// </summary>
    private static string? TryNominalizeVerb(string criterionText)
    {
        var firstSpace = criterionText.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return null;
        }

        var verb = criterionText[..firstSpace].ToLowerInvariant();
        var rest = criterionText[firstSpace..].Trim();

        var nominalization = verb switch
        {
            "elaborar" => "elaboração",
            "identificar" => "identificação",
            "descrever" => "descrição",
            "apresentar" => "apresentação",
            "analisar" => "análise",
            "comparar" => "comparação",
            "relacionar" => "relação entre",
            "propor" => "proposição",
            "planejar" => "planejamento",
            "demonstrar" => "demonstração",
            "explicar" => "explicação",
            "classificar" => "classificação",
            "definir" => "definição",
            "organizar" => "organização",
            "criar" => "criação",
            "aplicar" => "aplicação",
            "adequar" => "adequação",
            "avaliar" => "avaliação",
            "indicar" => "indicação",
            "formular" => "formulação",
            "estruturar" => "estruturação",
            "listar" => "listagem",
            "justificar" => "justificativa",
            "responder" => "resposta referente a",
            _ => null
        };

        if (nominalization == null)
        {
            return null;
        }

        // Ajustar preposição: "elaboração um plano" → "elaboração de um plano"
        var needsDe = !rest.StartsWith("de ", StringComparison.OrdinalIgnoreCase) &&
                      !rest.StartsWith("da ", StringComparison.OrdinalIgnoreCase) &&
                      !rest.StartsWith("do ", StringComparison.OrdinalIgnoreCase) &&
                      !rest.StartsWith("dos ", StringComparison.OrdinalIgnoreCase) &&
                      !rest.StartsWith("das ", StringComparison.OrdinalIgnoreCase) &&
                      !rest.StartsWith("entre ", StringComparison.OrdinalIgnoreCase) &&
                      !rest.StartsWith("sobre ", StringComparison.OrdinalIgnoreCase) &&
                      !nominalization.EndsWith(" ");

        return needsDe
            ? $"{nominalization} de {LowercaseFirst(rest)}"
            : $"{nominalization} {LowercaseFirst(rest)}";
    }

    /// <summary>
    /// Remove o verbo inicial de um critério, retornando o complemento.
    /// Ex: "Elaborar um plano de gerenciamento" → "um plano de gerenciamento"
    /// </summary>
    private static string RemoveLeadingVerb(string criterionText)
    {
        var firstSpace = criterionText.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return LowercaseFirst(criterionText);
        }

        return LowercaseFirst(criterionText[(firstSpace + 1)..].Trim());
    }

    // --- Métodos legacy preservados para referência e debug ---

    /// <summary>
    /// [LEGACY] Feedback antigo baseado em concatenação de evidências.
    /// Preservado para comparação durante transição. Será removido em versão futura.
    /// </summary>
    private static string BuildStructuredFeedbackLegacy(
        string assignmentName,
        IReadOnlyList<GradingCriterionAnalysis> criteria,
        int wordCount)
    {
        var sb = new System.Text.StringBuilder();

        var strongCriteria = criteria.Where(c => (c.SuggestedPoints ?? 0) >= (c.MaxPoints ?? 0) * 0.5m).ToList();
        var weakCriteria = criteria.Where(c => c.TeacherReviewRequired).ToList();

        if (strongCriteria.Count >= criteria.Count / 2)
        {
            sb.Append("Bom trabalho. ");
        }
        else
        {
            sb.Append("O trabalho apresenta esforco na resolucao da atividade. ");
        }

        if (strongCriteria.Count > 0)
        {
            var strongTexts = strongCriteria
                .Select(c => SummarizeCriterionPositive(c))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (strongTexts.Count > 0)
            {
                sb.Append(JoinNaturalList(strongTexts));
                sb.Append(". ");
            }
        }

        if (weakCriteria.Count > 0)
        {
            sb.Append("Para melhorar, ");
            var improvementTexts = weakCriteria
                .Select(c => SummarizeCriterionImprovement(c))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (improvementTexts.Count > 0)
            {
                sb.Append(JoinNaturalList(improvementTexts));
                sb.Append('.');
            }
            else
            {
                sb.Append("revise os aspectos que precisam de maior aprofundamento.");
            }
        }
        else if (strongCriteria.Count < criteria.Count)
        {
            sb.Append("Considere revisar os pontos que podem ser aprofundados.");
        }

        return sb.ToString().Trim();
    }

    private static string SummarizeCriterionPositive(GradingCriterionAnalysis criterion)
    {
        if (!string.IsNullOrWhiteSpace(criterion.EvidenceFound) &&
            !criterion.EvidenceFound.StartsWith("Nao foram", StringComparison.OrdinalIgnoreCase))
        {
            return LowercaseFirst(criterion.EvidenceFound.TrimEnd('.'));
        }

        return $"demonstrou compreensao do aspecto '{LowercaseFirst(criterion.CriterionText)}'";
    }

    private static string SummarizeCriterionImprovement(GradingCriterionAnalysis criterion)
    {
        if (!string.IsNullOrWhiteSpace(criterion.Gaps))
        {
            return LowercaseFirst(criterion.Gaps.TrimEnd('.'));
        }

        return $"aprofunde o aspecto '{LowercaseFirst(criterion.CriterionText)}'";
    }

    private static string JoinNaturalList(IReadOnlyList<string> items)
    {
        return items.Count switch
        {
            0 => string.Empty,
            1 => UppercaseFirst(items[0]),
            2 => $"{UppercaseFirst(items[0])} e {items[1]}",
            _ => $"{UppercaseFirst(string.Join(", ", items.Take(items.Count - 1)))} e {items[^1]}"
        };
    }

    private static string LowercaseFirst(string text)
    {
        return string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text[1..];
    }

    private static string UppercaseFirst(string text)
    {
        return string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string CleanAssignmentName(string name)
    {
        var cleaned = name;
        var prefixMatch = Regex.Match(cleaned, @"^(?:Envio\s*(?:da\s*)?atividade|Atividade)\s*[-–:]\s*", RegexOptions.IgnoreCase);
        if (prefixMatch.Success)
        {
            cleaned = cleaned[prefixMatch.Length..];
        }

        if (Regex.IsMatch(cleaned, @"^\d+$"))
        {
            return "Atividade"; // Fallback se so sobrou numero
        }

        return cleaned;
    }

    private static string BuildTeacherNotes(
        IReadOnlyList<GradingCriterionAnalysis> criteria,
        string submissionText,
        decimal confidence,
        CriteriaSourceKind criteriaSource,
        bool hasMaxGrade,
        decimal effectiveMaxGrade)
    {
        var reviews = criteria.Where(c => c.TeacherReviewRequired).ToList();
        var snippetLength = Math.Min(submissionText.Length, 500);
        var snippet = submissionText[..snippetLength];

        var notes = new System.Text.StringBuilder();
        notes.Append($"Analise estruturada gerada automaticamente. Confianca estimada: {confidence * 100:0}%. ");

        if (criteria.Count > 0)
        {
            notes.Append($"Criterios para revisao manual: {reviews.Count}/{criteria.Count}. ");
        }

        // Informar a origem dos criterios
        switch (criteriaSource)
        {
            case CriteriaSourceKind.TeacherOverride:
                notes.Append("Criterios modificados ou substituidos pelas instrucoes diretas do professor (TeacherInstructions usadas). ");
                break;
            case CriteriaSourceKind.Formal:
                notes.Append("Criterios extraidos de rubrica/criterios formais. ");
                break;
            case CriteriaSourceKind.Approximate:
                notes.Append("Enunciado da atividade selecionado como contexto. INSTRUCAO PARA A IA: O contexto selecionado nao possui rubrica formal. Use o texto extraido do enunciado como contexto pedagogico principal, e nao como lista direta de criterios. Primeiro interprete o enunciado, identifique comandos avaliativos e expectativas de resposta, ignorando cabecalhos, rodapes, logos, nomes, turma, enderecos e textos administrativos. Em seguida, compare a submissao do aluno com essas expectativas e gere feedback especifico, com pontos fortes e lacunas reais. So transforme em criterios avaliativos os comandos pedagogicos detectados ou derivados, nunca use as linhas brutas do documento como criterio. ");
                break;
            case CriteriaSourceKind.None:
                notes.Append("Nenhum criterio formal, instrucao ou descricao encontrados. Analise baseada apenas no conteudo da submissao. ");
                break;
        }

        if (!hasMaxGrade)
        {
            notes.Append("Escala de nota nao identificada. Nota sugerida indisponivel. ");
        }
        else if (effectiveMaxGrade > 0 && criteriaSource != CriteriaSourceKind.Formal)
        {
            notes.Append($"Valor da atividade extraido do contexto: {effectiveMaxGrade} pontos. Confirme se esta correto. ");
        }

        if (confidence < 0.5m)
        {
            notes.Append("Baixa confianca: revise manualmente a nota sugerida, pois a submissao tem pouca extensao textual ou baixa cobertura dos criterios. ");
        }

        notes.Append($"Trecho inicial da submissao: {snippet}");
        return notes.ToString();
    }

    private static decimal CalculateConfidence(
        int wordCount,
        IReadOnlyList<GradingCriterionAnalysis> criteria)
    {
        if (criteria.Count == 0)
        {
            return 0.3m;
        }

        var reviewRatio = (decimal)criteria.Count(c => c.TeacherReviewRequired) / criteria.Count;
        if (wordCount < 50 || reviewRatio >= 0.75m)
        {
            return 0.35m;
        }

        if (reviewRatio >= 0.5m)
        {
            return 0.5m;
        }

        return 0.7m;
    }

    /// <summary>
    /// Parseia criterios de uma string, rejeitando fragmentos inuteis.
    /// Requer pelo menos 3 palavras uteis (>3 chars) por criterio.
    /// Limita a 6 criterios para manter qualidade.
    /// </summary>
    private static IReadOnlyList<string> ParseCriteria(string rubricOrCriteria)
    {
        return rubricOrCriteria
            .Split(['\n', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ').Trim())
            .Where(c => CountUsefulWords(c) >= 3)
            .Take(6)
            .ToArray();
    }

    /// <summary>
    /// Conta palavras uteis (>3 chars) em um texto.
    /// </summary>
    private static int CountUsefulWords(string text)
    {
        return text.Split([' ', ',', ';', '.', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Count(word => word.Length > 3);
    }

    /// <summary>
    /// Extrai palavras significativas de um criterio para matching.
    /// Usa palavras >3 chars, ate 8 por criterio.
    /// </summary>
    private static string[] ExtractSignificantWords(string criterion)
    {
        return criterion.ToLowerInvariant()
            .Split([' ', ',', ';', '.', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length > 3)
            .Where(word => !IsStopWord(word))
            .Take(8)
            .ToArray();
    }

    private static bool IsStopWord(string word)
    {
        return word is "para" or "como" or "pela" or "pelo" or "pelas" or "pelos"
            or "mais" or "cada" or "este" or "esta" or "esse" or "essa"
            or "entre" or "sobre" or "tambem" or "também" or "deve" or "devem"
            or "sendo" or "foram" or "será" or "sera" or "quando" or "onde"
            or "toda" or "todo" or "todas" or "todos" or "seus" or "suas";
    }

    /// <summary>
    /// Constroi evidencia concreta com palavras-chave encontradas na entrega.
    /// </summary>
    private static string BuildConcreteEvidence(string criterion, string[] matchedWords)
    {
        if (matchedWords.Length == 0)
        {
            return "Nao foram identificados elementos diretamente relacionados a este aspecto no texto analisado.";
        }

        var keywordsDisplay = matchedWords.Length <= 4
            ? string.Join(", ", matchedWords)
            : string.Join(", ", matchedWords.Take(4)) + " e outros";

        return $"O texto aborda elementos relacionados ({keywordsDisplay}), demonstrando compreensao do aspecto solicitado";
    }

    /// <summary>
    /// Constroi lacunas com orientacao especifica sobre o que melhorar.
    /// </summary>
    private static string BuildConcreteGaps(string criterion, string[] criterionWords, string[] matchedWords)
    {
        var missingWords = criterionWords.Except(matchedWords).Take(3).ToArray();
        if (missingWords.Length > 0)
        {
            var missing = string.Join(", ", missingWords);
            return $"Aprofunde os aspectos relacionados a {missing} para atender melhor ao criterio";
        }

        return $"Aprofunde a abordagem sobre '{LowercaseFirst(criterion)}' com mais detalhamento";
    }

    /// <summary>
    /// Estima nota baseada na cobertura de palavras-chave do enunciado
    /// na submissão, para o cenário Approximate sem critérios parseados.
    /// </summary>
    private static decimal EstimateGradeFromCoverage(string contextText, string submissionText, decimal maxGrade)
    {
        var contextWords = contextText.ToLowerInvariant()
            .Split([' ', ',', ';', '.', ':', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 4) // Só palavras significativas
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();

        if (contextWords.Length == 0)
        {
            return 0m;
        }

        var submissionLower = submissionText.ToLowerInvariant();
        var matchedWords = contextWords.Count(w => submissionLower.Contains(w));
        var coverage = (decimal)matchedWords / contextWords.Length;

        // Escala conservadora: coverage 100% → 70% da nota máxima (pois é aproximado)
        var estimatedGrade = Math.Round(maxGrade * Math.Min(coverage * 0.7m, 0.7m), 2);
        return Math.Max(0m, estimatedGrade);
    }

    private static int CountWords(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static decimal? TryExtractMaxGrade(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = MaxGradeRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups["grade"].Value.Replace(',', '.');
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var grade) && grade > 0
            ? grade
            : null;
    }

    [GeneratedRegex(@"(?i)\b(?:valor(?:\s+da\s+atividade)?|nota\s*m[aá]xima|pontua[cç][aã]o|vale)\s*:?\s*(?<grade>\d+(?:[\.,]\d+)?)\s*(?:pontos?|pts?|%)?")]
    private static partial Regex MaxGradeRegex();

    private sealed record CriteriaResolution(string? Text, CriteriaSourceKind Source);

    private enum CriteriaSourceKind { None, Formal, Approximate, TeacherOverride }
}
