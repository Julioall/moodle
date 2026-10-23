using System.Collections.Concurrent;
using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAssignmentEffectiveDeadlinesReaderTests
{
    [Fact]
    public async Task Reads_effective_deadlines_by_assignment_and_user_and_preserves_confirmed_absence()
    {
        var client = new ParticipantClient(userId => userId switch
        {
            "21" => 1_800_000_000L,
            "22" => 0L,
            _ => 1_900_000_000L
        });
        var requests = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["12"] = ["21", "22", "23"]
        };

        var result = Assert.Single(await MoodleAssignmentEffectiveDeadlinesReader.ReadAsync(
            client, Credentials, requests, CancellationToken.None));

        Assert.Equal("12", result.AssignmentId);
        Assert.True(result.Complete);
        Assert.Equal(3, client.Requests.Count);
        Assert.True(result.TryGetDeadline("21", out var extended));
        Assert.True(extended.Known);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), extended.DueAt);
        Assert.True(result.TryGetDeadline("22", out var noDeadline));
        Assert.True(noDeadline.Known);
        Assert.Null(noDeadline.DueAt);
        Assert.All(client.Requests, request =>
        {
            Assert.Equal("mod_assign_get_participant", request.FunctionName);
            Assert.Equal("12", Convert.ToString(request.Parameters["assignid"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains(Convert.ToString(request.Parameters["userid"], System.Globalization.CultureInfo.InvariantCulture),
                new[] { "21", "22", "23" });
            Assert.Equal(false, request.Parameters["embeduser"]);
        });
    }

    [Fact]
    public async Task Empty_submitted_user_set_is_complete_without_moodle_calls()
    {
        var client = new ParticipantClient(_ => 1_800_000_000L);
        var requests = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["12"] = []
        };

        var result = Assert.Single(await MoodleAssignmentEffectiveDeadlinesReader.ReadAsync(
            client, Credentials, requests, CancellationToken.None));

        Assert.True(result.Complete);
        Assert.Empty(result.DeadlinesByUser);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Limits_parallel_reads_and_keeps_successes_when_one_participant_fails()
    {
        var client = new ParticipantClient(
            userId => 1_800_000_000L,
            failingUserId: "99",
            delayMilliseconds: 5);
        var requests = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["12"] = Enumerable.Range(1, 25).Select(id => id.ToString()).Append("99").ToArray()
        };

        var result = Assert.Single(await MoodleAssignmentEffectiveDeadlinesReader.ReadAsync(
            client, Credentials, requests, CancellationToken.None));

        Assert.False(result.Complete);
        Assert.Equal(26, client.Requests.Count);
        Assert.InRange(client.MaximumConcurrency, 1, 4);
        Assert.True(result.TryGetDeadline("1", out var known));
        Assert.True(known.Known);
        Assert.True(result.TryGetDeadline("99", out var unknown));
        Assert.False(unknown.Known);
        Assert.NotNull(result.ErrorCode);
    }

    [Theory]
    [InlineData("missing-user", "{\"duedate\":1800000000}")]
    [InlineData("wrong-user", "{\"id\":22,\"duedate\":1800000000}")]
    [InlineData("negative-date", "{\"id\":21,\"duedate\":-1}")]
    [InlineData("overflow-date", "{\"id\":21,\"duedate\":9223372036854775807}")]
    public async Task Invalid_participant_response_is_unknown(string _, string json)
    {
        var client = new ParticipantClient(_ => 1_800_000_000L, responseJson: json);
        var requests = new Dictionary<string, IReadOnlyCollection<string>> { ["12"] = ["21"] };

        var result = Assert.Single(await MoodleAssignmentEffectiveDeadlinesReader.ReadAsync(
            client, Credentials, requests, CancellationToken.None));

        Assert.False(result.Complete);
        Assert.True(result.TryGetDeadline("21", out var deadline));
        Assert.False(deadline.Known);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_unknown()
    {
        var client = new ParticipantClient(_ => 1_800_000_000L, cancel: true);
        var requests = new Dictionary<string, IReadOnlyCollection<string>> { ["12"] = ["21"] };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MoodleAssignmentEffectiveDeadlinesReader.ReadAsync(client, Credentials, requests, CancellationToken.None));
    }

    private static MoodleCredentials Credentials => new(
        "client", "connection", "default", "https://moodle.example.invalid", "synthetic-user", "synthetic-password", "target", false);

    private sealed class ParticipantClient(
        Func<string, long> dueDate,
        string? failingUserId = null,
        int delayMilliseconds = 0,
        string? responseJson = null,
        bool cancel = false) : IMoodleRestClient
    {
        private int _active;
        private int _maximumConcurrency;

        public ConcurrentBag<(string FunctionName, IReadOnlyDictionary<string, object?> Parameters)> Requests { get; } = [];
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            Requests.Add((functionName, parameters));
            Assert.Equal("mod_assign_get_participant", functionName);
            var userId = Convert.ToString(parameters["userid"], System.Globalization.CultureInfo.InvariantCulture)!;
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                if (cancel) throw new OperationCanceledException();
                if (userId == failingUserId) throw new InvalidOperationException("Falha sintética de leitura.");
                if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken);
                return responseJson is null
                    ? JsonSerializer.SerializeToElement(new { id = int.Parse(userId), duedate = dueDate(userId) })
                    : JsonDocument.Parse(responseJson).RootElement.Clone();
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, cancellationToken);

        public Task<JsonElement> CallWriteAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Escrita não permitida nesta fixture.");

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrency);
                if (value <= current || Interlocked.CompareExchange(ref _maximumConcurrency, value, current) == current)
                {
                    return;
                }
            }
        }
    }
}
