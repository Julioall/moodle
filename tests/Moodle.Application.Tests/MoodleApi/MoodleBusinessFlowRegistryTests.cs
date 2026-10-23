using Moodle.Application.MoodleApi;

namespace Moodle.Application.Tests.MoodleApi;

public sealed class MoodleBusinessFlowRegistryTests
{
    [Fact]
    public void Evaluate_SelecionaEstrategiaPreferencialOuFallback()
    {
        var registry = new MoodleBusinessFlowRegistry();
        var preferred = Profile("core_course_get_enrolled_courses_by_timeline_classification", "core_enrol_get_users_courses");
        var fallback = Profile("core_enrol_get_users_courses");

        Assert.Equal("timeline", registry.Evaluate("listar_cursos_ativos", preferred).SelectedStrategy);
        Assert.Equal("enrolled_courses_fallback", registry.Evaluate("listar_cursos_ativos", fallback).SelectedStrategy);
    }

    [Fact]
    public void Evaluate_InformaFuncoesAusentesQuandoFluxoIndisponivel()
    {
        var availability = new MoodleBusinessFlowRegistry().Evaluate("listar_entregas_aguardando_correcao", Profile());

        Assert.False(availability.IsAvailable);
        Assert.Contains("mod_assign_get_submissions", availability.MissingFunctions);
    }

    private static MoodleFunctionProfile Profile(params string[] functions) => new(
        "connection", "goias", null, null, null,
        functions.Select(name => new MoodleFunctionDescriptor(name, MoodleFunctionRisk.Read, true)).ToArray(),
        DateTimeOffset.UtcNow);
}
