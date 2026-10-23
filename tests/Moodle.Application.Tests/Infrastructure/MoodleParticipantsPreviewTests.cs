using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleParticipantsPreviewTests
{
    [Fact]
    public async Task StaffOnlySampleDoesNotScanMorePagesToFindStudents()
    {
        using var transport = new ReportTransportFixture();
        transport.Response = (_, _) => ReportTransportFixture.Json(JsonSerializer.Serialize(Enumerable.Range(1, 20)
            .Select(id => new { id, fullname = "Synthetic staff", roles = new[] { new { roleid = 3, shortname = "editingteacher", name = "Staff" } } })));
        var sample = await Gateway(transport).GetCourseParticipantsSampleAsync("100", "42", 20, default);
        Assert.Empty(sample.Participants);
        Assert.False(sample.Complete);
        Assert.Equal(20, sample.RawCandidateCount);
        var call = Assert.Single(transport.Calls);
        Assert.True(call.NoRetry);
        Assert.Contains(call.Parameters, p => p.Value == "limitnumber");
        Assert.Equal("20", call.Parameters["options[1][value]"]);
        Assert.Equal("0", call.Parameters["options[0][value]"]);
    }

    [Fact]
    public async Task SampleCountsUnknownAndUnidentifiableParticipantsWithoutReturningThem()
    {
        using var transport = new ReportTransportFixture();
        transport.Response = (_, _) => ReportTransportFixture.Json("""
            [
              {"id":1,"fullname":"Synthetic custom role","roles":[{"roleid":9,"shortname":"localrole","name":"Learner assistant"}]},
              {"id":2,"fullname":"","roles":[]},
              {"id":3,"fullname":"Synthetic student","roles":[{"roleid":5,"shortname":"student","name":"Student"}]}
            ]
            """);

        var sample = await Gateway(transport).GetCourseParticipantsSampleAsync("100", "42", 20, default);

        Assert.Equal(["3"], sample.Participants.Select(participant => participant.UserId));
        Assert.Equal(2, sample.Diagnostics.UnclassifiedRoleCount);
    }

    [Fact]
    public async Task StatusFallbackUsesAtMostTwoAttemptsAndKeepsUncertainty()
    {
        using var transport = new ReportTransportFixture();
        transport.Response = (_, _) => transport.Calls.Count == 1
            ? ReportTransportFixture.Json("""{"exception":"required_capability_exception","errorcode":"nopermissions","message":"Synthetic denial"}""")
            : ReportTransportFixture.Json("""[{"id":7,"fullname":"Synthetic student","roles":[{"roleid":5,"shortname":"student","name":"Student"}],"groups":[]}]""");
        var sample = await Gateway(transport).GetCourseParticipantsSampleAsync("100", "42", 20, default);
        Assert.Equal(2, transport.Calls.Count);
        Assert.Equal(2, sample.HttpAttempts);
        Assert.False(sample.Complete);
        Assert.True(sample.Diagnostics.UsedStatusFilterFallback);
        Assert.False(Assert.Single(sample.Participants).EnrollmentStatusKnown);
        Assert.All(transport.Calls, call => Assert.True(call.NoRetry));
        Assert.DoesNotContain(transport.Calls[1].Parameters, p => p.Value == "onlyactive");
    }

    [Fact]
    public async Task IgnoredLimitReturnsUnavailableWithoutPublishingUnboundedPopulation()
    {
        using var transport = new ReportTransportFixture();
        transport.Response = (_, _) => ReportTransportFixture.Json(JsonSerializer.Serialize(Enumerable.Range(1, 21)
            .Select(id => new { id, fullname = "Synthetic student" })));
        var sample = await Gateway(transport).GetCourseParticipantsSampleAsync("100", "42", 20, default);
        Assert.Empty(sample.Participants);
        Assert.Equal("preview_limit_unsupported", sample.UnavailableReason);
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task AbortReachesTransportAndHasNoFallback()
    {
        using var transport = new ReportTransportFixture();
        using var abort = new CancellationTokenSource();
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gateway(transport)
            .GetCourseParticipantsSampleAsync("100", "42", 20, abort.Token));
        Assert.Empty(transport.Calls);
    }
    private static MoodleParticipantsGateway Gateway(ReportTransportFixture fixture) =>
        new(Options.Create(new MoodleApiOptions()), fixture.Credentials, fixture.Rest);
}
