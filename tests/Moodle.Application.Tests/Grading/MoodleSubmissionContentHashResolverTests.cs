using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Domain;

namespace Moodle.Application.Tests.Grading;

public sealed class MoodleSubmissionContentHashResolverTests
{
    private static readonly DateTimeOffset ModifiedAt = new(2026, 9, 23, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ResolveAsync_WhenKnownHashesAreMissing_UsesCurrentMoodleFileBytes()
    {
        var content = "reviewed file"u8.ToArray();
        var submissions = new FakeAssignmentSubmissionsGateway(
            [CreateSubmission(fileCount: 1, [CreateFile("submission.pdf")])]);
        var files = new FakeSubmissionFileGateway(content);
        var resolver = CreateResolver(submissions, files, new GradingLimitsOptions());

        var actual = await resolver.ResolveAsync("teacher", "501", "101", 777, [], CancellationToken.None);

        Assert.Equal("4998cc01179c7571ecf04d496551017d5946be63d270194c2201194102070093", actual.Hash);
        Assert.Equal(1, files.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_WhenCurrentSubmissionIsProvided_DoesNotReadTheAssignmentAgain()
    {
        var current = CreateSubmission(fileCount: 1, [CreateFile("submission.pdf")]);
        var submissions = new FakeAssignmentSubmissionsGateway([current]);
        var files = new FakeSubmissionFileGateway("file"u8.ToArray());
        var resolver = CreateResolver(submissions, files, new GradingLimitsOptions());

        await resolver.ResolveAsync(
            "teacher",
            "501",
            "101",
            777,
            [],
            CancellationToken.None,
            current);

        Assert.Equal(0, submissions.CallCount);
        Assert.Equal(1, files.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_WhenCurrentFileBytesChanged_UsesTheChangedBytesInTheFingerprint()
    {
        var currentContent = "new Moodle file"u8.ToArray();
        var submissions = new FakeAssignmentSubmissionsGateway(
            [CreateSubmission(fileCount: 1, [CreateFile("submission.pdf")])]);
        var resolver = CreateResolver(submissions, new FakeSubmissionFileGateway(currentContent), new GradingLimitsOptions());

        var actual = await resolver.ResolveAsync("teacher", "501", "101", 777, [], CancellationToken.None);

        Assert.NotEqual("8f152f35cc36d11e75d1f73e3e348d5d9daf0fad3eeb3f9b1cf558a90add2117", actual.Hash);
        Assert.Equal("39e6a112ed7ddfd930fd60340be08e420186622898510d3a5dbc165ca7b3bbaa", actual.Hash);
    }

    [Fact]
    public async Task ResolveAsync_WhenMoodleFileCountExceedsAvailableFileReferences_FailsInsteadOfHashingPartialEvidence()
    {
        var submissions = new FakeAssignmentSubmissionsGateway(
            [CreateSubmission(fileCount: 2, [CreateFile("only-one.pdf")])]);
        var files = new FakeSubmissionFileGateway("file"u8.ToArray());
        var resolver = CreateResolver(submissions, files, new GradingLimitsOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync("teacher", "501", "101", 777, [], CancellationToken.None));

        Assert.Equal(0, files.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_WhenFileReferencesConflictWithZeroDeclaredFiles_FailsInsteadOfTrustingEmptyHashes()
    {
        var submissions = new FakeAssignmentSubmissionsGateway(
            [CreateSubmission(fileCount: 0, [CreateFile("unexpected.pdf")])]);
        var files = new FakeSubmissionFileGateway("file"u8.ToArray());
        var resolver = CreateResolver(submissions, files, new GradingLimitsOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync("teacher", "501", "101", 777, [], CancellationToken.None));

        Assert.Equal(0, files.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_ForTextOnlySubmission_DoesNotDownloadFiles()
    {
        const string onlineText = "Resposta digitada no Moodle";
        var submissions = new FakeAssignmentSubmissionsGateway(
            [CreateSubmission(fileCount: 0, [], onlineText)]);
        var files = new FakeSubmissionFileGateway("unused"u8.ToArray());
        var resolver = CreateResolver(submissions, files, new GradingLimitsOptions());

        var actual = await resolver.ResolveAsync("teacher", "501", "101", 777, [], CancellationToken.None);

        Assert.Equal("9e015f3992228a7e0c02ec3077b99cc03a32e6f0e6d7329a3e09834e41fc7b6f", actual.Hash);
        Assert.Equal(0, files.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_WhenFileCountExceedsConfiguredLimit_FailsBeforeDownloading()
    {
        var submissions = new FakeAssignmentSubmissionsGateway(
            [CreateSubmission(fileCount: 1, [CreateFile("submission.pdf")])]);
        var files = new FakeSubmissionFileGateway("file"u8.ToArray());
        var resolver = CreateResolver(
            submissions,
            files,
            new GradingLimitsOptions { MaxFilesPerSubmission = 0 });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync("teacher", "501", "101", 777, [], CancellationToken.None));

        Assert.Equal(0, files.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_WhenDownloadedFileIsTruncated_FailsInsteadOfSealingPartialBytes()
    {
        var submissions = new FakeAssignmentSubmissionsGateway(
            [CreateSubmission(fileCount: 1, [CreateFile("submission.pdf")])]);
        var files = new FakeSubmissionFileGateway("partial"u8.ToArray(), truncated: true);
        var resolver = CreateResolver(submissions, files, new GradingLimitsOptions());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            resolver.ResolveAsync("teacher", "501", "101", 777, [], CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ResolveAsync_WhenDownloadedBytesExceedLimitsOrMetadataIsInvalid_Fails(
        bool exceedsLimit,
        bool wrongHash,
        bool wrongSize)
    {
        var content = exceedsLimit ? "larger than configured limit"u8.ToArray() : "actual bytes"u8.ToArray();
        var submissions = new FakeAssignmentSubmissionsGateway(
            [CreateSubmission(fileCount: 1, [CreateFile("submission.pdf")])]);
        var files = new FakeSubmissionFileGateway(
            content,
            sizeBytes: wrongSize ? content.LongLength + 1 : content.LongLength,
            sha256: wrongHash ? new string('0', 64) : Sha256(content));
        var resolver = CreateResolver(
            submissions,
            files,
            new GradingLimitsOptions { MaxResourceBytes = exceedsLimit ? 4 : 1024 });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            resolver.ResolveAsync("teacher", "501", "101", 777, [], CancellationToken.None));
    }

    private static MoodleSubmissionContentHashResolver CreateResolver(
        IMoodleAssignmentSubmissionsGateway submissions,
        IMoodleSubmissionFileGateway files,
        GradingLimitsOptions limits)
    {
        var services = new ServiceCollection();
        services.AddSingleton(submissions);
        services.AddSingleton(files);
        services.AddSingleton<IOptions<GradingLimitsOptions>>(Options.Create(limits));
        using var provider = services.BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<MoodleSubmissionContentHashResolver>(provider);
    }

    private static AssignmentSubmissionRecord CreateSubmission(
        int fileCount,
        IReadOnlyList<AssignmentSubmissionFile> files,
        string? onlineText = null) => new(
            SubmissionId: "777",
            UserId: "101",
            Status: "submitted",
            GradingStatus: null,
            CreatedAt: ModifiedAt,
            ModifiedAt: ModifiedAt,
            AttemptNumber: 2,
            FileCount: fileCount,
            HasOnlineText: !string.IsNullOrWhiteSpace(onlineText),
            Files: files,
            OnlineText: onlineText);

    private static AssignmentSubmissionFile CreateFile(string filename) => new(
        filename,
        "application/pdf",
        12,
        $"https://moodle.example/pluginfile.php/{filename}");

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed class FakeAssignmentSubmissionsGateway(
        IReadOnlyList<AssignmentSubmissionRecord> submissions) : IMoodleAssignmentSubmissionsGateway
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
            string userExternalId,
            string assignmentId,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(submissions);
        }
    }

    private sealed class FakeSubmissionFileGateway(
        byte[] content,
        bool truncated = false,
        long? sizeBytes = null,
        string? sha256 = null) : IMoodleSubmissionFileGateway
    {
        public int CallCount { get; private set; }

        public Task<SubmissionFileDownloadResult> DownloadFileAsync(
            string userExternalId,
            string fileUrl,
            string filename,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new SubmissionFileDownloadResult(
                filename,
                "application/pdf",
                sizeBytes ?? content.LongLength,
                sha256 ?? Sha256(content),
                content,
                truncated));
        }
    }
}
