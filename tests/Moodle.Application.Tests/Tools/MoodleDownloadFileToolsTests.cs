using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Domain;
using Moodle.Presentation.Tools;

namespace Moodle.Application.Tests.Tools;

public sealed class MoodleDownloadFileToolsTests
{
    [Fact]
    public async Task DownloadFile_OmittedNameReachesGatewayAsOmittedAndOutputUsesSafeUrlName()
    {
        var gateway = new RecordingFileGateway();
        var sut = new MoodleDownloadFileTools(gateway, new FakeCurrentUserContext(),
            Options.Create(new MoodleUniversalApiFeatureOptions { UniversalMoodleFileDownloadEnabled = true }),
            Options.Create(new GradingLimitsOptions()), new FakeAuditLogRepository(), new FakeMoodleConnectionSelection());
        var result = await sut.DownloadAsync("https://moodle.example/pluginfile.php/1/lesson.pdf?token=synthetic");
        Assert.False(result.IsError ?? false);
        Assert.Equal(string.Empty, gateway.LastFilename);
        Assert.Contains("lesson.pdf", result.StructuredContent!.ToString());
        Assert.DoesNotContain("synthetic", result.StructuredContent!.ToString());
    }

    [Fact]
    public void DownloadFile_ExposesAliasDaConexaoMoodle()
    {
        var method = typeof(MoodleDownloadFileTools).GetMethod(nameof(MoodleDownloadFileTools.DownloadAsync));

        Assert.NotNull(method);
        Assert.Contains(
            method!.GetParameters(),
            parameter => string.Equals(parameter.Name, "moodleAlias", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadFile_SelecionaAliasAntesDeResolverGateway()
    {
        var selection = new FakeMoodleConnectionSelection();
        var gateway = new RecordingFileGateway();
        var sut = new MoodleDownloadFileTools(
            gateway,
            new FakeCurrentUserContext(),
            Options.Create(new MoodleUniversalApiFeatureOptions { UniversalMoodleFileDownloadEnabled = true }),
            Options.Create(new GradingLimitsOptions()),
            new FakeAuditLogRepository(),
            selection);

        var result = await sut.DownloadAsync(
            "https://moodle.example/pluginfile.php/1/sap.pdf",
            "SAP 01.pdf",
            "FIEG");

        Assert.False(result.IsError ?? false);
        Assert.Equal("FIEG", selection.Alias);
        Assert.Equal("https://moodle.example/pluginfile.php/1/sap.pdf", gateway.LastFileUrl);
        Assert.Equal("SAP 01.pdf", gateway.LastFilename);
    }

    private sealed class FakeMoodleConnectionSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private sealed class FakeCurrentUserContext : ICurrentUserContext
    {
        public string Subject => "test-subject";

        public string? Email => null;

        public IReadOnlyCollection<string> Scopes => [];

        public bool HasScope(string scope) => true;
    }

    private sealed class RecordingFileGateway : IMoodleSubmissionFileGateway
    {
        public string? LastFileUrl { get; private set; }

        public string? LastFilename { get; private set; }

        public Task<SubmissionFileDownloadResult> DownloadFileAsync(
            string userExternalId,
            string fileUrl,
            string filename,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            LastFileUrl = fileUrl;
            LastFilename = filename;
            return Task.FromResult(new SubmissionFileDownloadResult(
                filename,
                "application/pdf",
                3,
                "abc",
                [1, 2, 3],
                false));
        }
    }

    private sealed class FakeAuditLogRepository : IMoodleAuditLogRepository
    {
        public Task AddAsync(MoodleAuditLog log, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<MoodleAuditLog>> ListByCorrelationIdAsync(
            string correlationId,
            int page,
            int pageSize,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);

        public Task<int> CountByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(
            Guid batchJobId,
            int page,
            int pageSize,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);

        public Task<int> CountByBatchJobIdAsync(Guid batchJobId, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
