using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class ReportScopeLiveTests
{
    [Fact]
    public async Task WarmCourseListCannotGrantCurrentAccessAndValidationNeverReadsStudentRoster()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var transport = new ReportTransportFixture();
        cache.Set("moodle:courses:synthetic-connection:100", (IReadOnlyList<CourseSummary>)[
            new("42", null, null, "Old name", null, 1, "Old category", null, null, true, null, null, null, null, null, null)]);
        transport.Response = (_, _) => ReportTransportFixture.Json("""[{"id":7,"fullname":"Current course"}]""");
        var gateway = new MoodleCoursesGateway(Options.Create(new MoodleApiOptions()), cache, transport.Credentials,
            transport.Rest, null!, null!, null!, null!);
        var accessible = await gateway.GetReportCoursesLiveAsync("100", ["42"], false, true, default);
        Assert.Empty(accessible);
        var call = Assert.Single(transport.Calls);
        Assert.Equal("core_enrol_get_users_courses", call.Function);
        Assert.True(call.NoRetry);
    }
}
