using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class ReportLiveGradebookTests
{
    [Fact]
    public async Task WarmIndividualAndScaleCachesCannotReplaceLiveValues()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var transport = new ReportTransportFixture();
        var value = 34;
        transport.Response = (function, _) => ReportTransportFixture.Json(function.StartsWith("mod_", StringComparison.Ordinal)
            ? $$"""{"quizzes":[{"id":1,"coursemodule":10,"grade":{{value}}}]}""" : Grades(value, false));
        var gateway = Gateway(transport, cache);
        var old = await gateway.GetStudentGradebookAsync("42", "7", default);
        value = 55;
        var current = await gateway.GetStudentGradebookAsync("42", "7", GradebookReadOptions.Live, default);
        Assert.Equal(34m, old.Items.Single(i => i.ItemType == "course").GradeRaw);
        Assert.Equal(55m, current.Items.Single(i => i.ItemType == "course").GradeRaw);
        Assert.Equal(55m, current.Items.Single(i => i.ItemType == "course").GradeMax);
        Assert.Equal(2, transport.Calls.Count(c => c.Function == "mod_quiz_get_quizzes_by_courses"));
        Assert.NotNull(current.ReadAt);
        Assert.Equal("activity_sum", current.ScaleSource);
    }

    [Fact]
    public async Task PreviewBypassesWarmCachesAndMakesNoHiddenActivityOrBulkRead()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var transport = new ReportTransportFixture();
        transport.Response = (function, _) => ReportTransportFixture.Json(function.StartsWith("mod_", StringComparison.Ordinal)
            ? """{"quizzes":[{"id":1,"coursemodule":10,"grade":34}]}""" : Grades(34, false));
        var gateway = Gateway(transport, cache);
        await gateway.GetStudentGradebookAsync("42", "7", default);
        var previous = transport.Calls.Count;
        transport.Response = (_, _) => ReportTransportFixture.Json(Grades(55, false));
        var preview = await gateway.GetStudentGradebookAsync("42", "7", GradebookReadOptions.Preview, default);
        var call = Assert.Single(transport.Calls.Skip(previous));
        Assert.Equal("gradereport_user_get_grade_items", call.Function);
        Assert.Equal("7", call.Parameters["userid"]);
        Assert.True(call.NoRetry);
        Assert.Equal(55m, preview.Items.Single(i => i.ItemType == "course").GradeRaw);
        Assert.Null(preview.Items.Single(i => i.ItemType == "course").GradeMax);
        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.GetCourseGradebookAsync("42", ["7"], null, GradebookReadOptions.Preview, default));
        Assert.Equal(previous + 1, transport.Calls.Count);
    }

    [Fact]
    public async Task WarmBulkAndIndividualFallbackCachesAreBypassedAndLiveFailureDoesNotReturnOldData()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var transport = new ReportTransportFixture();
        transport.Response = (_, _) => ReportTransportFixture.Json(Grades(10, true));
        var gateway = Gateway(transport, cache);
        await gateway.GetCourseGradebookAsync("42", ["7"], null, default);
        await gateway.GetStudentGradebookAsync("42", "7", default);
        transport.Response = (_, p) => p["userid"] == "0" ? ReportTransportFixture.Json("""{"usergrades":[]} """)
            : ReportTransportFixture.Json(Grades(80, true));
        var current = await gateway.GetCourseGradebookAsync("42", ["7"], null, GradebookReadOptions.Live, default);
        Assert.Equal(80m, current.Gradebooks["7"].Items.Single(i => i.ItemType == "course").GradeRaw);
        transport.Response = (_, _) => ReportTransportFixture.Json("{}", HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<MoodleApiException>(() => gateway.GetStudentGradebookAsync("42", "7", GradebookReadOptions.Live, default));
    }

    private static string Grades(int value, bool explicitScale) => $$"""
        {"usergrades":[{"userid":7,"gradeitems":[{"id":99,"itemtype":"course","itemname":"Total","graderaw":{{value}}{{(explicitScale ? ",\"grademax\":100" : "")}}},{"id":1,"itemtype":"mod","itemmodule":"quiz","iteminstance":"1","cmid":"10","graderaw":{{value}}}]}]}
        """;
    private static MoodleGradebookGateway Gateway(ReportTransportFixture fixture, IMemoryCache cache) =>
        new(Options.Create(new MoodleApiOptions()), fixture.Credentials, fixture.Rest, cache,
            courseGradeMaxGateway: new MoodleCourseGradeMaxGateway(Options.Create(new MoodleApiOptions()), fixture.Credentials, fixture.Rest, cache));
}
