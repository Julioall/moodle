using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Xunit;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class McpReadInvocationScopeTests
{
    [Theory]
    [InlineData("assignments", "wrapper", "assignments.submissions.list", true)]
    [InlineData("forums", "read", "forums.read", true)]
    [InlineData("discovery", "diagnostic", "connector.diagnostics.connection", true)]
    [InlineData("grading", "read", "grading.gradebook.get", false)]
    [InlineData("assignments", "controlled-write", "assignments.grade.publish", false)]
    [InlineData("reports", "read", "grading.report.read", false)]
    [InlineData("assignments", "unclassified", "assignments.unknown", false)]
    public void IsSafeReadTool_excludes_grading_and_write_scopes(
        string family,
        string kind,
        string canonicalOperation,
        bool expected)
    {
        Assert.Equal(expected, McpReadInvocationScope.IsSafeReadTool(family, kind, canonicalOperation));
    }

    [Fact]
    public async Task MoodleReadRetry_records_logical_retries_only_inside_mcp_read_scope()
    {
        var telemetry = new CapturingTelemetry();
        var scope = new McpReadInvocationScope(telemetry);
        var calls = 0;

        using (scope.Begin("get_course_module", "course.module.get"))
        {
            var result = await MoodleReadRetry.ExecuteAsync(
                _ =>
                {
                    calls++;
                    if (calls == 1)
                    {
                        throw new MoodleApiException(
                            MoodleErrorContract.NetworkError,
                            "transient failure",
                            connectionAlias: "private alias",
                            functionName: "core_course_get_contents");
                    }

                    return Task.FromResult("ok");
                },
                onRetry: null,
                CancellationToken.None);

            Assert.Equal("ok", result);
        }

        Assert.Equal(2, calls);
        var retry = Assert.Single(telemetry.Retries);
        Assert.Equal("get_course_module", retry.ToolName);
        Assert.Equal("course.module.get", retry.CanonicalOperation);
        Assert.Equal("core_course_get_contents", retry.FunctionName);
        Assert.Equal(1, retry.Attempt);
    }

    private sealed class CapturingTelemetry : IMoodleCallTelemetry
    {
        public List<RetryRecord> Retries { get; } = [];
        public void RecordMoodleWebServiceCall(string? connectionAlias = null) { }
        public void RecordMoodleWebServiceLogicalRetry(
            string? connectionAlias,
            string? functionName,
            string? toolName,
            string? canonicalOperation,
            int attempt) =>
            Retries.Add(new RetryRecord(toolName, canonicalOperation, functionName, attempt));
    }

    private sealed record RetryRecord(string? ToolName, string? CanonicalOperation, string? FunctionName, int Attempt);
}
