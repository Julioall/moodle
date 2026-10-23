using System.Collections.Generic;
using Moodle.Benchmarks.Cognitive;
using Xunit;

namespace Moodle.Application.Tests.Benchmarking;

public sealed class BenchmarkConnectionScoringTests
{
    [Fact]
    public void Score_ShouldPass_WhenExpectedConnectionAndSelectedConnectionMatch()
    {
        var task = new BenchmarkTask(
            Id: "test-connection-1",
            Category: "courses",
            Prompt: "No SENAI, liste meus cursos.",
            ExpectedIntent: "courses.list",
            AllowedOperations: new[] { "core_enrol_get_users_courses" },
            ForbiddenOperations: new string[0],
            RequiresCompleteDataset: false,
            ExpectedConnection: "senai"
        );

        var routing = new RoutingTrace(
            SelectedSkill: "moodle-core",
            SelectedIntent: "courses.list",
            SelectedOperation: "core_enrol_get_users_courses",
            SelectedConnection: "senai",
            Arguments: new Dictionary<string, object>(),
            ToolInvocations: new ToolInvocationTrace[0]
        );

        var execution = new ExecutionTrace(
            ConnectionId: System.Guid.Empty,
            RegistryOperation: "core_enrol_get_users_courses",
            PolicyDecision: "Allowed",
            MoodleCalls: 1,
            LatencyMs: 5,
            PromptTokens: 10,
            CompletionTokens: 20,
            TotalTokens: 30,
            ToolSchemaTokens: 0
        );

        var scorer = new BenchmarkScorer();
        var scoring = scorer.Score(task, routing, execution, resultContent: string.Empty);

        Assert.True(scoring.IntentAccuracy);
        Assert.True(scoring.RoutingAccuracy);
        Assert.True(scoring.ConnectionAccuracy);
        Assert.True(scoring.OverallSuccess);
        Assert.Equal(FailureTaxonomy.None, scoring.FailureReason);
    }

    [Fact]
    public void Score_ShouldFail_WrongConnection_WhenSelectedConnectionDiffersFromExpected()
    {
        var task = new BenchmarkTask(
            Id: "test-connection-2",
            Category: "courses",
            Prompt: "No FIEG, mostre o curso 33458.",
            ExpectedIntent: "courses.details",
            AllowedOperations: new[] { "core_course_get_courses_by_field" },
            ForbiddenOperations: new string[0],
            RequiresCompleteDataset: false,
            ExpectedConnection: "fieg"
        );

        var routing = new RoutingTrace(
            SelectedSkill: "moodle-core",
            SelectedIntent: "courses.details",
            SelectedOperation: "core_course_get_courses_by_field",
            SelectedConnection: "senai",
            Arguments: new Dictionary<string, object>(),
            ToolInvocations: new ToolInvocationTrace[0]
        );

        var execution = new ExecutionTrace(
            ConnectionId: System.Guid.Empty,
            RegistryOperation: "core_course_get_courses_by_field",
            PolicyDecision: "Allowed",
            MoodleCalls: 1,
            LatencyMs: 5,
            PromptTokens: 12,
            CompletionTokens: 25,
            TotalTokens: 37,
            ToolSchemaTokens: 0
        );

        var scorer = new BenchmarkScorer();
        var scoring = scorer.Score(task, routing, execution, resultContent: string.Empty);

        Assert.True(scoring.IntentAccuracy);
        Assert.True(scoring.RoutingAccuracy);
        Assert.False(scoring.ConnectionAccuracy);
        Assert.False(scoring.OverallSuccess);
        Assert.Equal(FailureTaxonomy.WrongConnection, scoring.FailureReason);
    }

    [Fact]
    public void Score_ShouldNotPenalize_WhenExpectedConnectionIsNull()
    {
        var task = new BenchmarkTask(
            Id: "test-connection-3",
            Category: "courses",
            Prompt: "Mostre meus cursos.",
            ExpectedIntent: "courses.list",
            AllowedOperations: new[] { "core_enrol_get_users_courses" },
            ForbiddenOperations: new string[0],
            RequiresCompleteDataset: false,
            ExpectedConnection: null
        );

        var routing = new RoutingTrace(
            SelectedSkill: "moodle-core",
            SelectedIntent: "courses.list",
            SelectedOperation: "core_enrol_get_users_courses",
            SelectedConnection: "senai",
            Arguments: new Dictionary<string, object>(),
            ToolInvocations: new ToolInvocationTrace[0]
        );

        var execution = new ExecutionTrace(
            ConnectionId: System.Guid.Empty,
            RegistryOperation: "core_enrol_get_users_courses",
            PolicyDecision: "Allowed",
            MoodleCalls: 1,
            LatencyMs: 5,
            PromptTokens: 10,
            CompletionTokens: 20,
            TotalTokens: 30,
            ToolSchemaTokens: 0
        );

        var scorer = new BenchmarkScorer();
        var scoring = scorer.Score(task, routing, execution, resultContent: string.Empty);

        Assert.True(scoring.IntentAccuracy);
        Assert.True(scoring.RoutingAccuracy);
        Assert.True(scoring.ConnectionAccuracy);
        Assert.True(scoring.OverallSuccess);
        Assert.Equal(FailureTaxonomy.None, scoring.FailureReason);
    }

    [Fact]
    public void ExtractConnectionFromArguments_ShouldUseMoodleAliasAndNotInferFromPrompt()
    {
        var args = new Dictionary<string, object>
        {
            ["moodleAlias"] = "senai",
            ["courseId"] = "8887"
        };

        var selected = OpenAIResponsesBenchmarkDriver.ExtractConnectionFromArguments(args);

        Assert.Equal("senai", selected);
    }
}
