using Moodle.Application.Abstractions;
using Moodle.Application.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class DiscoverMoodleGradingCapabilitiesQueryHandlerTests
{
    [Fact]
    public async Task Handle_ClassificaFuncoesCriticasDeCorrecao()
    {
        var gateway = new FakeMoodleGradingCapabilitiesGateway(
            "moodle_mobile_app",
            [
                "mod_assign_get_submissions",
                "mod_assign_get_submission_status",
                "mod_assign_get_grades",
                "mod_assign_save_grade",
                "core_files_get_files"
            ]);
        var sut = new DiscoverMoodleGradingCapabilitiesQueryHandler(gateway);

        var report = await sut.Handle(
            new DiscoverMoodleGradingCapabilitiesQuery("321"),
            CancellationToken.None);

        Assert.Equal("moodle_mobile_app", report.ServiceName);
        Assert.True(report.CanReadSubmissions);
        Assert.True(report.CanReadGrades);
        Assert.True(report.CanReadFiles);
        Assert.True(report.CanWriteIndividualGrades);
        Assert.False(report.CanWriteBatchGrades);
        Assert.Contains("mod_assign_save_grades", report.MissingFunctions);
        Assert.All(report.Functions, function => Assert.DoesNotContain("token", function.Name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeMoodleGradingCapabilitiesGateway(
        string serviceName,
        IReadOnlyCollection<string> functions) : IMoodleGradingCapabilitiesGateway
    {
        public Task<MoodleWebServiceFunctionCatalog> GetFunctionCatalogAsync(
            string userExternalId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new MoodleWebServiceFunctionCatalog(serviceName, functions));
        }
    }
}
