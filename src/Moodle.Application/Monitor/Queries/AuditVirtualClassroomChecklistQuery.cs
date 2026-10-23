using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Application.Monitor.Queries;

// ── Checklist types ───────────────────────────────────────────────────────────

/// <summary>
/// Resultado de um item do checklist de sala virtual SENAI CTM.
/// </summary>
public sealed record ChecklistItemResult(
    string ItemKey,
    string ItemDescription,
    string Status,          // "ok" | "ausente" | "incompleto" | "nao_verificavel"
    string? Observation);

public sealed record AuditVirtualClassroomChecklistResult(
    string CourseId,
    DateTimeOffset GeneratedAt,
    int TotalItems,
    int OkCount,
    int AusenteCount,
    int IncompletoCount,
    int NaoVerificavelCount,
    IReadOnlyList<ChecklistItemResult> Items,
    string? Warning);

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Audita a sala virtual contra o checklist padrão do Guia do Tutor SENAI CTM.
///
/// Estratégia:
/// 1. Busca conteúdos do curso (todas as seções e módulos via GetCourseContentsAsync).
/// 2. Para cada item do checklist, verifica presença por nome de módulo, tipo ou palavras-chave.
/// 3. Retorna status por item: ok, ausente, incompleto ou não verificável.
///
/// Limitações:
/// - Verifica apenas presença textual/tipo. Não valida conteúdo interno.
/// - Visibilidade da sala (sala_visivel) pode não estar disponível na API.
/// - Datas (datas_configuradas) verificadas pela presença de pelo menos uma data em módulos do tipo assign.
/// </summary>
public sealed record AuditVirtualClassroomChecklistQuery(
    string CourseId) : IRequest<AuditVirtualClassroomChecklistResult>;

public sealed class AuditVirtualClassroomChecklistQueryHandler(
    IMoodleCourseContentsGateway contentsGateway,
    IMoodleCurrentUserIdGateway currentUserIdGateway)
    : IRequestHandler<AuditVirtualClassroomChecklistQuery, AuditVirtualClassroomChecklistResult>
{
    private static readonly IReadOnlyList<string> AllModuleTypes = [];   // empty = all types

    public async Task<AuditVirtualClassroomChecklistResult> Handle(
        AuditVirtualClassroomChecklistQuery request,
        CancellationToken cancellationToken)
    {
        var currentUserExternalId = (await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken)).ToString();
        var now = DateTimeOffset.UtcNow;

        CourseContentsSummary contents;
        try
        {
            contents = await contentsGateway.GetCourseContentsAsync(
                userExternalId: currentUserExternalId,
                courseId: request.CourseId,
                moduleTypes: AllModuleTypes,
                includeHidden: false,
                onlyWithFiles: false,
                cancellationToken: cancellationToken);
        }
        catch
        {
            return new AuditVirtualClassroomChecklistResult(
                CourseId: request.CourseId, GeneratedAt: now,
                TotalItems: 0, OkCount: 0, AusenteCount: 0, IncompletoCount: 0, NaoVerificavelCount: 0,
                Items: [],
                Warning: "Não foi possível carregar os conteúdos do curso. Verifique permissões e configuração da API.");
        }

        // Flatten all modules from all sections
        var allModules = contents.Sections
            .SelectMany(s => s.Modules)
            .ToList();

        var checklistItems = new (string Key, string Description)[]
        {
            ("guia_estudante",          "Guia do Estudante publicado"),
            ("criterios_certificacao",  "Critérios de certificação visíveis"),
            ("plano_estudo",            "Plano de Estudo / Cronograma presente"),
            ("forum_apresentacao",      "Fórum de Apresentação aberto"),
            ("forum_duvidas",           "Fórum de Dúvidas aberto"),
            ("scorm_conteudo",          "Conteúdo interativo (SCORM ou equivalente) presente"),
            ("situacao_aprendizagem",   "Situação de Aprendizagem (SA) configurada"),
            ("datas_configuradas",      "Datas de abertura/encerramento configuradas em atividades"),
            ("sala_visivel",            "Sala visível para os estudantes"),
        };

        var items = checklistItems
            .Select(ci => EvaluateChecklistItem(ci.Key, ci.Description, allModules))
            .ToList();

        return new AuditVirtualClassroomChecklistResult(
            CourseId: request.CourseId,
            GeneratedAt: now,
            TotalItems: items.Count,
            OkCount: items.Count(i => i.Status == "ok"),
            AusenteCount: items.Count(i => i.Status == "ausente"),
            IncompletoCount: items.Count(i => i.Status == "incompleto"),
            NaoVerificavelCount: items.Count(i => i.Status == "nao_verificavel"),
            Items: items,
            Warning: null);
    }

    private static ChecklistItemResult EvaluateChecklistItem(
        string key, string description, List<CourseModuleSummary> allModules) =>
        key switch
        {
            "forum_apresentacao" => CheckForum(key, description, allModules,
                ["apresentação", "apresentacao", "apresentação de"]),

            "forum_duvidas" => CheckForum(key, description, allModules,
                ["dúvid", "duvid", "tira-dúvid", "tire dúvid", "perguntas"]),

            "scorm_conteudo" => CheckModuleType(key, description, allModules,
                types: ["scorm", "h5p", "lti"],
                fallbackKeywords: ["objeto de aprendizagem", "objeto interativo", "conteúdo interativo"]),

            "situacao_aprendizagem" => CheckModuleType(key, description, allModules,
                types: ["assign"],
                fallbackKeywords: ["sa ", "situação de aprendizagem", "atividade avaliativa"]),

            "datas_configuradas" => CheckDatesConfigured(key, description, allModules),

            "sala_visivel" => new ChecklistItemResult(key, description, "nao_verificavel",
                "Visibilidade da sala não é diretamente verificável via API de conteúdos. Verificar manualmente no painel do Moodle."),

            "guia_estudante" => CheckByKeyword(key, description, allModules,
                ["guia do estudante", "guia estudante"]),

            "criterios_certificacao" => CheckByKeyword(key, description, allModules,
                ["critério", "criterio", "certificação", "certificacao", "critérios de avaliação"]),

            "plano_estudo" => CheckByKeyword(key, description, allModules,
                ["plano de estudo", "plano de ação", "cronograma"]),

            _ => new ChecklistItemResult(key, description, "nao_verificavel",
                "Verificação automática não disponível para este item.")
        };

    private static ChecklistItemResult CheckForum(
        string key, string description, List<CourseModuleSummary> allModules, string[] keywords)
    {
        var forums = allModules
            .Where(m => string.Equals(m.ModuleType, "forum", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!forums.Any())
            return new ChecklistItemResult(key, description, "ausente",
                "Nenhum fórum encontrado no curso.");

        var found = forums.Any(f =>
            keywords.Any(kw => f.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)));

        return found
            ? new ChecklistItemResult(key, description, "ok", null)
            : new ChecklistItemResult(key, description, "incompleto",
                $"Fórum presente, mas nenhum com nome indicando '{description}'. " +
                $"Fóruns encontrados: {string.Join(", ", forums.Select(f => $"'{f.Name}'"))}.");
    }

    private static ChecklistItemResult CheckModuleType(
        string key, string description, List<CourseModuleSummary> allModules,
        string[] types, string[] fallbackKeywords)
    {
        var byType = allModules
            .Where(m => types.Any(t => string.Equals(m.ModuleType, t, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (byType.Any())
            return new ChecklistItemResult(key, description, "ok",
                $"{byType.Count} módulo(s) do tipo {string.Join("/", types)} encontrado(s).");

        var byKeyword = allModules
            .Where(m => fallbackKeywords.Any(kw =>
                m.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return byKeyword.Any()
            ? new ChecklistItemResult(key, description, "ok",
                $"Encontrado por palavra-chave no nome do módulo: '{byKeyword.First().Name}'.")
            : new ChecklistItemResult(key, description, "ausente",
                $"Nenhum módulo do tipo {string.Join("/", types)} ou com palavras-chave esperadas foi encontrado.");
    }

    private static ChecklistItemResult CheckDatesConfigured(
        string key, string description, List<CourseModuleSummary> allModules)
    {
        var assigns = allModules
            .Where(m => string.Equals(m.ModuleType, "assign", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!assigns.Any())
            return new ChecklistItemResult(key, description, "nao_verificavel",
                "Nenhuma atividade do tipo 'assign' encontrada. Verificar se SAs estão configuradas.");

        var withDates = assigns.Count(a => a.Dates.Count > 0);

        if (withDates == 0)
            return new ChecklistItemResult(key, description, "ausente",
                $"{assigns.Count} SA(s) encontrada(s), porém nenhuma com datas configuradas.");

        if (withDates < assigns.Count)
            return new ChecklistItemResult(key, description, "incompleto",
                $"{withDates} de {assigns.Count} SA(s) com datas configuradas. Verificar as restantes.");

        return new ChecklistItemResult(key, description, "ok",
            $"Todas as {assigns.Count} SA(s) têm datas configuradas.");
    }

    private static ChecklistItemResult CheckByKeyword(
        string key, string description, List<CourseModuleSummary> allModules, string[] keywords)
    {
        if (!keywords.Any())
            return new ChecklistItemResult(key, description, "nao_verificavel",
                "Verificação automática não disponível para este item. Verificar manualmente.");

        var found = allModules.Any(m =>
            keywords.Any(kw => m.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)));

        return found
            ? new ChecklistItemResult(key, description, "ok", null)
            : new ChecklistItemResult(key, description, "ausente",
                $"Nenhum módulo com nome contendo as palavras-chave esperadas foi encontrado ({string.Join(", ", keywords)}).");
    }
}
