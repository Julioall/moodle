using System.Security.Cryptography;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Moodle.Domain;

namespace Moodle.Application.Tests.Grading;

public sealed class CompareSelectedModelAnswerQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsNeutralSignalForExactBytesWithoutExposingContentOrHash()
    {
        var bytes = "synthetic exact model response"u8.ToArray();
        var fixture = CreateFixture(bytes, bytes);

        var result = await fixture.Handler.Handle(fixture.Query, CancellationToken.None);

        Assert.True(result.Identical);
        Assert.Equal("submission_identical_to_template", result.SignalCode);
        Assert.Equal("explicit_request_selection", result.SelectionSource);
        Assert.Equal("sha256_exact", result.ComparisonMethod);
        Assert.Equal("10", result.CourseId);
        Assert.Equal("501", result.AssignmentId);
        Assert.Equal(2, result.EvidenceReferences.Count);
        Assert.DoesNotContain("synthetic exact model response", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(SHA256.HashData(bytes)), System.Text.Json.JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_DifferentBytesDoNotEmitIdentitySignal()
    {
        var fixture = CreateFixture(
            "synthetic student response"u8.ToArray(),
            "synthetic model answer"u8.ToArray());

        var result = await fixture.Handler.Handle(fixture.Query, CancellationToken.None);

        Assert.False(result.Identical);
        Assert.Null(result.SignalCode);
    }

    [Fact]
    public async Task Handle_RejectsModelFromAnotherActivityBeforeReadingFiles()
    {
        var fixture = CreateFixture(
            "synthetic student response"u8.ToArray(),
            "synthetic model answer"u8.ToArray(),
            modelAssignmentId: 777);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Handler.Handle(fixture.Query, CancellationToken.None));

        Assert.Equal(0, fixture.Gateway.ReadCount);
    }

    [Fact]
    public async Task Handle_RejectsResourcesFromDifferentOwnersBeforeReadingFiles()
    {
        var fixture = CreateFixture(
            "synthetic student response"u8.ToArray(),
            "synthetic model answer"u8.ToArray(),
            modelOwner: "different-teacher");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Handler.Handle(fixture.Query, CancellationToken.None));

        Assert.Equal(0, fixture.Gateway.ReadCount);
    }

    [Theory]
    [InlineData("client-other", "connection-a")]
    [InlineData("client-a", "connection-other")]
    public async Task Handle_RejectsResourcesFromDifferentClientOrConnectionBeforeReadingFiles(
        string modelClientId,
        string modelConnectionId)
    {
        var fixture = CreateFixture(
            "synthetic student response"u8.ToArray(),
            "synthetic model answer"u8.ToArray(),
            modelClientId: modelClientId,
            modelConnectionId: modelConnectionId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Handler.Handle(fixture.Query, CancellationToken.None));

        Assert.Equal(0, fixture.Gateway.ReadCount);
    }

    [Fact]
    public async Task Handle_RejectsNonAssignmentContextModelResource()
    {
        var fixture = CreateFixture(
            "synthetic student response"u8.ToArray(),
            "synthetic model answer"u8.ToArray(),
            modelResourceType: "course_resource");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Handler.Handle(fixture.Query, CancellationToken.None));

        Assert.Equal(0, fixture.Gateway.ReadCount);
    }

    private static Fixture CreateFixture(
        byte[] submissionBytes,
        byte[] modelBytes,
        long modelAssignmentId = 501,
        string modelOwner = "teacher-a",
        string modelResourceType = "assignment_context_attachment",
        string modelClientId = "client-a",
        string modelConnectionId = "connection-a")
    {
        var submission = CreateResource(
            "submission_attachment",
            submissionId: 2001,
            studentId: 101,
            submissionBytes);
        var model = CreateResource(
            modelResourceType,
            submissionId: null,
            studentId: null,
            modelBytes,
            assignmentId: modelAssignmentId,
            ownerSubject: modelOwner,
            clientId: modelClientId,
            connectionId: modelConnectionId);
        var resources = new InMemoryMoodleResourceRepository([submission, model]);
        var gateway = new InMemoryMoodleResourceGateway(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [MoodleResourceUri.Create(submission.ResourceId)] = submissionBytes,
            [MoodleResourceUri.Create(model.ResourceId)] = modelBytes
        });
        var handler = new CompareSelectedModelAnswerQueryHandler(resources, gateway);
        var query = new CompareSelectedModelAnswerQuery(
            "10",
            "501",
            MoodleResourceUri.Create(submission.ResourceId),
            MoodleResourceUri.Create(model.ResourceId));
        return new Fixture(handler, query, gateway);
    }

    private static MoodleResource CreateResource(
        string resourceType,
        long? submissionId,
        long? studentId,
        byte[] bytes,
        long assignmentId = 501,
        string ownerSubject = "teacher-a",
        string clientId = "client-a",
        string connectionId = "connection-a") => new()
    {
        ResourceId = Guid.NewGuid().ToString("N"),
        ClientId = clientId,
        ConnectionId = connectionId,
        MoodleAlias = "default",
        OwnerSubject = ownerSubject,
        ResourceType = resourceType,
        CourseId = 10,
        AssignmentId = assignmentId,
        SubmissionId = submissionId,
        StudentId = studentId,
        Filename = "synthetic.txt",
        MimeType = "text/plain",
        SizeBytes = bytes.LongLength,
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        RemoteFileReference = "inline://synthetic.txt",
        InlineContent = bytes,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
    };

    private sealed record Fixture(
        CompareSelectedModelAnswerQueryHandler Handler,
        CompareSelectedModelAnswerQuery Query,
        InMemoryMoodleResourceGateway Gateway);

    private sealed class InMemoryMoodleResourceRepository(IReadOnlyList<MoodleResource> resources) : IMoodleResourceRepository
    {
        public Task RegisterAsync(MoodleResource resource, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<MoodleResource?> FindAsync(string resourceId, CancellationToken cancellationToken) =>
            Task.FromResult(resources.SingleOrDefault(resource => resource.ResourceId == resourceId));

        public Task<IReadOnlyList<MoodleResource>> ListBySubmissionAsync(
            string clientId,
            string connectionId,
            long submissionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodleResource>>(
                resources.Where(resource => resource.SubmissionId == submissionId).ToArray());

        public Task<bool> ExistsAndNotExpiredAsync(string resourceId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(resources.Any(resource => resource.ResourceId == resourceId && !resource.IsExpired(now)));

        public Task<int> RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryMoodleResourceGateway(IReadOnlyDictionary<string, byte[]> contentByUri) : IMoodleResourceGateway
    {
        public int ReadCount { get; private set; }

        public Task<MoodleResourceDescriptor> RegisterAsync(MoodleResourceRegistration request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MoodleResourceReadResult> ReadAsync(string uri, CancellationToken cancellationToken)
        {
            ReadCount++;
            var bytes = contentByUri[uri];
            return Task.FromResult(new MoodleResourceReadResult(
                uri,
                "text/plain",
                bytes,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }

        public Task<IReadOnlyList<MoodleResourceDescriptor>> ExpandZipAsync(string uri, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodleResourceDescriptor>>([]);
    }
}
