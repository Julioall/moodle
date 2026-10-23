using Moodle.Application.Abstractions;
using Moodle.Application.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingTechnicalDiscoveryQueryHandlerTests
{
    [Fact]
    public async Task Handle_ConsolidaDescobertaTecnicaSemExporToken()
    {
        var gateway = new FakeMoodleGradingCapabilitiesGateway(
            "moodle_mobile_app",
            [
                "mod_assign_get_assignments",
                "mod_assign_get_submissions",
                "mod_assign_get_submission_status",
                "mod_assign_get_grades",
                "mod_assign_save_grade",
                "mod_assign_save_grades",
                "core_grading_get_definitions",
                "core_files_get_files"
            ]);
        var credentials = new FakeMoodleCredentialsProvider(canWrite: true);
        var environment = new FakeGradingTechnicalDiscoveryEnvironment(
            assignmentGradeWriteEnabled: true,
            assignmentFeedbackWriteEnabled: true,
            allowServiceTokenForReadOnlyQueries: true);
        var sut = new GradingTechnicalDiscoveryQueryHandler(gateway, credentials, environment);

        var report = await sut.Handle(
            new GradingTechnicalDiscoveryQuery("321"),
            CancellationToken.None);

        Assert.Equal("moodle_mobile_app", report.ServiceName);
        Assert.Equal("requires_real_moodle_probe", report.OverallStatus);
        Assert.Empty(report.BlockingIssues);
        Assert.Equal("requires_submission_file_probe", report.Attachments.Status);
        Assert.Contains("core_files_get_files", report.Attachments.Evidence);
        Assert.Equal("ready_for_sandbox_probe", report.GradeWrite.Status);
        Assert.Contains("mod_assign_save_grade", report.GradeWrite.Evidence);
        Assert.Equal("unknown_requires_sandbox", report.Permissions.Status);
        Assert.Equal("requires_assignment_probe", report.RubricsAndScales.Status);
        Assert.Equal("user_token", report.WriteToken.Mode);
        Assert.True(report.WriteToken.ConnectorCanWrite);
        Assert.DoesNotContain("token=", report.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_BloqueiaEscritaQuandoModAssignSaveGradeOuPermissaoFaltam()
    {
        var gateway = new FakeMoodleGradingCapabilitiesGateway(
            "moodle_mobile_app",
            [
                "mod_assign_get_submissions",
                "mod_assign_get_submission_status",
                "mod_assign_get_grades"
            ]);
        var credentials = new FakeMoodleCredentialsProvider(canWrite: false);
        var environment = new FakeGradingTechnicalDiscoveryEnvironment(
            assignmentGradeWriteEnabled: true,
            assignmentFeedbackWriteEnabled: true,
            allowServiceTokenForReadOnlyQueries: false);
        var sut = new GradingTechnicalDiscoveryQueryHandler(gateway, credentials, environment);

        var report = await sut.Handle(
            new GradingTechnicalDiscoveryQuery("321"),
            CancellationToken.None);

        Assert.Equal("blocked", report.OverallStatus);
        Assert.Equal("blocked", report.GradeWrite.Status);
        Assert.Equal("user_token", report.WriteToken.Mode);
        Assert.Contains(report.BlockingIssues, issue => issue.Contains("mod_assign_save_grade", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("A conexao Moodle atual nao permite escrita.", report.BlockingIssues);
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

    private sealed class FakeMoodleCredentialsProvider(bool canWrite) : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new MoodleCredentials(
                "client-1",
                "connection-1",
                "goias",
                "https://moodle.example.test",
                "teacher",
                "secret",
                "moodle",
                canWrite));
        }
    }

    private sealed class FakeGradingTechnicalDiscoveryEnvironment(
        bool assignmentGradeWriteEnabled,
        bool assignmentFeedbackWriteEnabled,
        bool allowServiceTokenForReadOnlyQueries) : IGradingTechnicalDiscoveryEnvironment
    {
        public bool AssignmentGradeWriteEnabled { get; } = assignmentGradeWriteEnabled;

        public bool AssignmentFeedbackWriteEnabled { get; } = assignmentFeedbackWriteEnabled;

        public bool AllowServiceTokenForReadOnlyQueries { get; } = allowServiceTokenForReadOnlyQueries;
    }
}
