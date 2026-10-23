using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleCourseGradeMaxGatewayTests
{
    [Fact]
    public async Task Deriva_o_total_a_partir_dos_maximos_dos_quizzes_visiveis()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new ModuleSettingsRestClient();
        var gateway = new MoodleCourseGradeMaxGateway(
            Options.Create(new MoodleApiOptions()),
            new CredentialsProvider(),
            restClient,
            cache);
        var items = new[]
        {
            new GradebookItem("course", "Total", "course", "", null, 49m, "49,00", 0m, null, null, null, null, null, null, null),
            new GradebookItem("quiz-1", "Questionário 1", "mod", "quiz", null, 24m, "24,00", 0m, null, null, null, null, null, null, null, "19008", "1108048"),
            new GradebookItem("quiz-2", "Questionário 2", "mod", "quiz", null, 10m, "10,00", 0m, null, null, null, null, null, null, null, "19009", "1108049"),
            new GradebookItem("scorm", "Conteúdo do Curso", "mod", "scorm", null, 0m, "0,00", 0m, null, null, null, null, null, null, null, "15914", "1108047"),
        };

        var result = await gateway.ResolveAsync("10824", items, CancellationToken.None);
        var cachedResult = await gateway.ResolveAsync("10824", items, CancellationToken.None);

        Assert.Equal(49m, result.MaxGrade);
        Assert.Equal("activity_sum", result.Source);
        Assert.Equal("grade_max_fallback_activity_sum", result.Warning);
        Assert.Equal(result, cachedResult);
        Assert.Equal(1, restClient.QuizCalls);
        Assert.Equal(0, restClient.AssignmentCalls);
    }

    [Fact]
    public async Task Nao_infere_quando_existe_modulo_avaliativo_desconhecido()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var restClient = new ModuleSettingsRestClient();
        var gateway = new MoodleCourseGradeMaxGateway(
            Options.Create(new MoodleApiOptions()),
            new CredentialsProvider(),
            restClient,
            cache);

        var result = await gateway.ResolveAsync("10824", [
            new GradebookItem("course", "Total", "course", "", null, 20m, null, 0m, null, null, null, null, null, null, null),
            new GradebookItem("lesson-1", "Aula", "mod", "lesson", null, 20m, null, 0m, null, null, null, null, null, null, null),
        ], CancellationToken.None);

        Assert.Null(result.MaxGrade);
        Assert.Equal("grade_max_fallback_unknown_module", result.Warning);
        Assert.Equal(0, restClient.QuizCalls);
    }

    [Fact]
    public async Task Nao_infere_quando_a_nota_observada_excede_a_soma_dos_maximos()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gateway = new MoodleCourseGradeMaxGateway(
            Options.Create(new MoodleApiOptions()),
            new CredentialsProvider(),
            new ModuleSettingsRestClient(),
            cache);

        var result = await gateway.ResolveAsync("10824", [
            new GradebookItem("course", "Total", "course", "", null, 50m, null, 0m, null, null, null, null, null, null, null),
            new GradebookItem("quiz-1", "Questionário 1", "mod", "quiz", null, 24m, null, 0m, null, null, null, null, null, null, null, "19008", "1108048"),
            new GradebookItem("quiz-2", "Questionário 2", "mod", "quiz", null, 25m, null, 0m, null, null, null, null, null, null, null, "19009", "1108049"),
        ], CancellationToken.None);

        Assert.Null(result.MaxGrade);
        Assert.Equal("grade_max_fallback_score_exceeds_sum", result.Warning);
    }

    private sealed class CredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials(
                "client", "connection", "SENAI", "https://moodle.example", "user", "password", "SENAI", false));
    }

    private sealed class ModuleSettingsRestClient : IMoodleRestClient
    {
        public int QuizCalls { get; private set; }
        public int AssignmentCalls { get; private set; }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, true, cancellationToken);

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            var json = functionName switch
            {
                "mod_quiz_get_quizzes_by_courses" => QuizPayload(),
                "mod_assign_get_assignments" => AssignmentPayload(),
                _ => "{}",
            };
            using var document = JsonDocument.Parse(json);
            return Task.FromResult(document.RootElement.Clone());
        }

        private string QuizPayload()
        {
            QuizCalls++;
            return "{\"quizzes\":[{\"id\":19008,\"coursemodule\":1108048,\"grade\":24},{\"id\":19009,\"coursemodule\":1108049,\"grade\":25}]}";
        }

        private string AssignmentPayload()
        {
            AssignmentCalls++;
            return "{\"courses\":[{\"assignments\":[]}]}";
        }
    }
}
