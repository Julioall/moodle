namespace Moodle.Application.MoodleApi;

public sealed record MoodleBusinessFlowStrategy(
    string FlowName,
    string StrategyName,
    int Priority,
    IReadOnlySet<string> RequiredFunctions,
    IReadOnlySet<string>? OptionalFunctions = null);

public sealed record BusinessFlowAvailability(
    string FlowName,
    bool IsAvailable,
    string? SelectedStrategy,
    IReadOnlyList<string> MissingFunctions,
    IReadOnlyList<string> OptionalFunctions,
    string? Reason);

public interface IMoodleBusinessFlowRegistry
{
    BusinessFlowAvailability Evaluate(string flowName, MoodleFunctionProfile profile);

    MoodleBusinessFlowStrategy? ResolveStrategy(string flowName, MoodleFunctionProfile profile);

    IReadOnlyCollection<BusinessFlowAvailability> EvaluateAll(MoodleFunctionProfile profile);
}

public sealed class MoodleBusinessFlowRegistry : IMoodleBusinessFlowRegistry
{
    private static readonly IReadOnlyList<MoodleBusinessFlowStrategy> Strategies =
    [
        Strategy("listar_cursos_ativos", "timeline", 100, ["core_course_get_enrolled_courses_by_timeline_classification"]),
        Strategy("listar_cursos_ativos", "enrolled_courses_fallback", 50, ["core_enrol_get_users_courses"]),
        Strategy("consultar_curso", "course_by_field", 100, ["core_course_get_courses_by_field"]),
        Strategy("consultar_curso", "enrolled_courses_fallback", 40, ["core_enrol_get_users_courses"]),
        Strategy("buscar_cursos", "course_search", 100, ["core_course_search_courses"]),
        Strategy("buscar_cursos", "course_by_field", 80, ["core_course_get_courses_by_field"]),
        Strategy("buscar_cursos", "enrolled_courses_fallback", 40, ["core_enrol_get_users_courses"]),
        Strategy("listar_cursos_categoria", "course_by_category", 100, ["core_course_get_courses_by_field"]),
        Strategy("listar_entregas_aguardando_correcao", "assign_submissions", 100, ["mod_assign_get_assignments", "mod_assign_get_submissions"])
    ];

    public BusinessFlowAvailability Evaluate(string flowName, MoodleFunctionProfile profile)
    {
        var candidates = Strategies.Where(strategy => string.Equals(strategy.FlowName, flowName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(strategy => strategy.Priority)
            .ToArray();
        if (candidates.Length == 0)
        {
            return new BusinessFlowAvailability(flowName, false, null, [], [], "Fluxo Moodle nao registrado.");
        }

        var available = profile.Functions.Where(function => function.IsAvailable)
            .Select(function => function.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = candidates.FirstOrDefault(strategy => strategy.RequiredFunctions.All(available.Contains));
        if (selected is not null)
        {
            return new BusinessFlowAvailability(
                selected.FlowName,
                true,
                selected.StrategyName,
                [],
                selected.OptionalFunctions?.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
                null);
        }

        var missing = candidates.SelectMany(strategy => strategy.RequiredFunctions)
            .Where(function => !available.Contains(function))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(function => function, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new BusinessFlowAvailability(
            candidates[0].FlowName,
            false,
            null,
            missing,
            [],
            "Nenhuma estrategia compativel com as funcoes disponiveis.");
    }

    public MoodleBusinessFlowStrategy? ResolveStrategy(string flowName, MoodleFunctionProfile profile)
    {
        var availability = Evaluate(flowName, profile);
        if (!availability.IsAvailable || availability.SelectedStrategy is null)
        {
            return null;
        }

        return Strategies.First(strategy =>
            string.Equals(strategy.FlowName, flowName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(strategy.StrategyName, availability.SelectedStrategy, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyCollection<BusinessFlowAvailability> EvaluateAll(MoodleFunctionProfile profile) =>
        Strategies.Select(strategy => strategy.FlowName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(flowName => flowName, StringComparer.OrdinalIgnoreCase)
            .Select(flowName => Evaluate(flowName, profile))
            .ToArray();

    private static MoodleBusinessFlowStrategy Strategy(
        string flowName,
        string strategyName,
        int priority,
        IEnumerable<string> requiredFunctions,
        IEnumerable<string>? optionalFunctions = null) => new(
            flowName,
            strategyName,
            priority,
            new HashSet<string>(requiredFunctions, StringComparer.OrdinalIgnoreCase),
            optionalFunctions is null ? null : new HashSet<string>(optionalFunctions, StringComparer.OrdinalIgnoreCase));
}
