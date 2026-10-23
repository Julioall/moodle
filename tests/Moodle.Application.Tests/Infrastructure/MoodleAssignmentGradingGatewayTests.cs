using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Infrastructure;
using Xunit;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAssignmentGradingGatewayTests
{
    [Fact]
    public async Task SaveGradeAsync_FeedbackOnly_UsesMoodleNotSetSentinelInsteadOfZero()
    {
        var restClient = new CapturingRestClient();
        var gateway = new MoodleAssignmentGradingGateway(
            Options.Create(new MoodleApiOptions()),
            new WritableCredentialsProvider(),
            restClient);

        await gateway.SaveGradeAsync(
            "teacher-1",
            new AssignmentGradeWriteRequest(
                "117487",
                "356968",
                Grade: null,
                FeedbackText: "Feedback revisado.",
                AttemptNumber: 0,
                AddAttempt: false,
                ApplyToAll: false,
                WorkflowState: "graded"),
            CancellationToken.None);

        Assert.NotNull(restClient.LastWriteParameters);
        Assert.Equal("-1", restClient.LastWriteParameters!["grade"]);
        Assert.NotEqual("0", restClient.LastWriteParameters["grade"]);
        Assert.Equal("Feedback revisado.", restClient.LastWriteParameters["plugindata[assignfeedbackcomments_editor][text]"]);
    }

    [Fact]
    public async Task SaveGradeAsync_NumericZero_PreservesTheRealAcademicGrade()
    {
        var restClient = new CapturingRestClient();
        var gateway = new MoodleAssignmentGradingGateway(
            Options.Create(new MoodleApiOptions()),
            new WritableCredentialsProvider(),
            restClient);

        await gateway.SaveGradeAsync(
            "teacher-1",
            new AssignmentGradeWriteRequest(
                "117487",
                "356968",
                Grade: 0m,
                FeedbackText: "Nota zero revisada.",
                AttemptNumber: 0,
                AddAttempt: false,
                ApplyToAll: false,
                WorkflowState: "graded"),
            CancellationToken.None);

        Assert.Equal("0", restClient.LastWriteParameters!["grade"]);
    }

    private sealed class WritableCredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials(
                "client", "connection", "alias", "https://moodle.example", "user", "password", "alias", true));
    }

    private sealed class CapturingRestClient : IMoodleRestClient
    {
        public IReadOnlyDictionary<string, object?>? LastWriteParameters { get; private set; }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A grading write must use CallWriteAsync.");

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A grading write must use CallWriteAsync.");

        public Task<JsonElement> CallWriteAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            Assert.Equal("mod_assign_save_grade", functionName);
            LastWriteParameters = new Dictionary<string, object?>(parameters);
            return Task.FromResult(JsonSerializer.SerializeToElement<object?>(null));
        }
    }
}
