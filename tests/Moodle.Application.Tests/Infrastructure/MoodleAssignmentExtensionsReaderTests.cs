using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAssignmentExtensionsReaderTests
{
    [Fact]
    public async Task Reads_flags_in_chunks_without_per_student_calls()
    {
        var client = new FlagsClient();
        var ids = Enumerable.Range(1, 51).Select(id => id.ToString()).ToArray();
        var results = await MoodleAssignmentExtensionsReader.ReadAsync(client, Credentials, ids, CancellationToken.None);
        Assert.Equal(new[] { 50, 1 }, client.RequestSizes);
        Assert.Equal(51, results.Count);
        Assert.All(results, result => Assert.True(result.Complete));
    }

    [Fact]
    public async Task Failed_flags_read_preserves_explicit_unknown_without_retrying_per_student()
    {
        var client = new FlagsClient { Error = new InvalidOperationException("Falha sintética permanente.") };
        var results = await MoodleAssignmentExtensionsReader.ReadAsync(client, Credentials, ["12", "13"], CancellationToken.None);
        Assert.Single(client.RequestSizes);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => { Assert.False(result.Complete); Assert.NotNull(result.ErrorCode); });
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_unknown()
    {
        var client = new FlagsClient { Error = new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            MoodleAssignmentExtensionsReader.ReadAsync(client, Credentials, ["12"], CancellationToken.None));
    }

    private static MoodleCredentials Credentials => new("client", "connection", "default",
        "https://moodle.example.invalid", "synthetic-user", "synthetic-password", "target", false);

    private sealed class FlagsClient : IMoodleRestClient
    {
        public List<int> RequestSizes { get; } = [];
        public Exception? Error { get; init; }
        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName,
            IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            Assert.Equal("mod_assign_get_user_flags", functionName);
            RequestSizes.Add(parameters.Count);
            if (Error is not null) throw Error;
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                assignments = parameters.Values.Select(id => new { assignmentid = id, userflags = Array.Empty<object>() }).ToArray(),
                warnings = Array.Empty<object>()
            }));
        }
        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName,
            IReadOnlyDictionary<string, object?> parameters, bool allowServiceToken, CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, cancellationToken);
        public Task<JsonElement> CallWriteAsync(MoodleCredentials connection, string functionName,
            IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Escrita não permitida nesta fixture.");
    }

    [Fact]
    public void Parses_zero_positive_and_absent_user_from_complete_list()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            assignments = new[] { new { assignmentid = 12, userflags = new[] {
                new { userid = 21, extensionduedate = 0L }, new { userid = 22, extensionduedate = 1800000000L } } } },
            warnings = Array.Empty<object>()
        });
        var result = Assert.Single(MoodleAssignmentExtensionsReader.Parse(payload, ["12"]));
        Assert.True(result.Complete);
        Assert.True(result.TryGetExtension("21", out var zero));
        Assert.Null(zero);
        Assert.True(result.TryGetExtension("22", out var positive));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), positive);
        Assert.True(result.TryGetExtension("23", out var absent));
        Assert.Null(absent);
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("1", false)]
    [InlineData("unexpected", false)]
    public void No_flags_warning_is_distinct_from_permission_or_unknown_warning(string code, bool complete)
    {
        var payload = JsonSerializer.SerializeToElement(new { assignments = Array.Empty<object>(),
            warnings = new[] { new { itemid = 12, warningcode = code } } });
        var result = Assert.Single(MoodleAssignmentExtensionsReader.Parse(payload, ["12"]));
        Assert.Equal(complete, result.Complete);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"userid\":21}")]
    [InlineData("{\"userid\":21,\"extensionduedate\":-1}")]
    [InlineData("{\"userid\":21,\"extensionduedate\":9223372036854775807}")]
    public void Malformed_flag_never_proves_absence(string flag)
    {
        using var document = JsonDocument.Parse("{\"assignments\":[{\"assignmentid\":12,\"userflags\":[" + flag + "]}]}");
        var result = Assert.Single(MoodleAssignmentExtensionsReader.Parse(document.RootElement, ["12"]));
        Assert.False(result.Complete);
        Assert.False(result.TryGetExtension("21", out _));
    }

    [Fact]
    public void Omitted_assignment_is_unknown_and_extra_assignment_is_not_inserted()
    {
        using var document = JsonDocument.Parse("{\"assignments\":[{\"assignmentid\":99,\"userflags\":[]}]}");
        var result = Assert.Single(MoodleAssignmentExtensionsReader.Parse(document.RootElement, ["12"]));
        Assert.Equal("12", result.AssignmentId);
        Assert.False(result.Complete);
    }
}
