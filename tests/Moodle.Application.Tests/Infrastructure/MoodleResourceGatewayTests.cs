using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleResourceGatewayTests
{
    [Fact]
    public async Task ReadAsync_RevalidatesConnectionAndReturnsOriginalBytes()
    {
        await using var db = CreateDb();
        var files = new FileGateway();
        var credentials = new Credentials("client-a", "connection-a");
        var gateway = CreateGateway(db, files, credentials);
        var descriptor = await gateway.RegisterAsync(new MoodleResourceRegistration("submission_attachment", "atividade.pdf", "application/pdf", "https://moodle.example/pluginfile.php/1/a.pdf?token=never-persist"), CancellationToken.None);

        var result = await gateway.ReadAsync(descriptor.Uri, CancellationToken.None);

        Assert.Equal(files.Bytes, result.Content);
        Assert.Equal("application/pdf", result.MimeType);
        Assert.DoesNotContain("token", (await db.MoodleResources.SingleAsync()).RemoteFileReference, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_RejectsResourceFromAnotherConnection()
    {
        await using var db = CreateDb();
        var registered = CreateGateway(db, new FileGateway(), new Credentials("client-a", "connection-a"));
        var descriptor = await registered.RegisterAsync(new MoodleResourceRegistration("submission_attachment", "a.txt", "text/plain", "https://moodle.example/pluginfile.php/1/a.txt"), CancellationToken.None);
        var reader = CreateGateway(db, new FileGateway(), new Credentials("client-b", "connection-b"));

        var exception = await Assert.ThrowsAsync<MoodleResourceException>(() => reader.ReadAsync(descriptor.Uri, CancellationToken.None));
        Assert.Equal("RESOURCE_FORBIDDEN", exception.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_RestoresPersistedAliasBeforeResolvingConnection()
    {
        await using var db = CreateDb();
        var selection = new FakeSelection { Alias = "FIEG" };
        var files = new FileGateway();
        var credentials = new AliasCredentials(selection);
        var gateway = CreateGateway(db, files, credentials, selection: selection);
        var descriptor = await gateway.RegisterAsync(
            new MoodleResourceRegistration("submission_attachment", "a.txt", "text/plain", "https://fieg.example/pluginfile.php/1/a.txt"),
            CancellationToken.None);

        selection.Alias = "default";
        var result = await gateway.ReadAsync(descriptor.Uri, CancellationToken.None);

        Assert.Equal("FIEG", selection.Alias);
        Assert.Equal(files.Bytes, result.Content);
    }

    [Fact]
    public async Task ReadAsync_RejectsAnotherUserOnTheSameConnectorConnection()
    {
        await using var db = CreateDb();
        var credentials = new Credentials("client-a", "connection-a");
        var registered = CreateGateway(db, new FileGateway(), credentials, user: new User("teacher-a"));
        var descriptor = await registered.RegisterAsync(new MoodleResourceRegistration("submission_attachment", "a.txt", "text/plain", "https://moodle.example/pluginfile.php/1/a.txt"), CancellationToken.None);
        var reader = CreateGateway(db, new FileGateway(), credentials, user: new User("teacher-b"));

        var exception = await Assert.ThrowsAsync<MoodleResourceException>(() => reader.ReadAsync(descriptor.Uri, CancellationToken.None));

        Assert.Equal("RESOURCE_FORBIDDEN", exception.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_SharedHashCacheReusesVerifiedBytesOnlyAfterOwnerAuthorization()
    {
        await using var db = CreateDb();
        using var contentCache = new MoodleResourceContentCache(TimeProvider.System);
        var credentials = new Credentials("client-a", "connection-a");
        var files = new FileGateway();
        var knownHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(files.Bytes)).ToLowerInvariant();
        var requestA = new MoodleResourceRegistration(
            "submission_attachment", "a.pdf", "application/pdf", "https://moodle.example/pluginfile.php/1/a.pdf",
            Sha256: knownHash);
        var requestB = requestA with { Filename = "b.pdf", RemoteFileReference = "https://moodle.example/pluginfile.php/1/b.pdf" };
        var ownerA = CreateGateway(db, files, credentials, user: new User("teacher-a"), contentCache: contentCache);
        var descriptorA = await ownerA.RegisterAsync(requestA, CancellationToken.None);
        var first = await ownerA.ReadAsync(descriptorA.Uri, CancellationToken.None);

        var ownerB = CreateGateway(db, files, credentials, user: new User("teacher-b"), contentCache: contentCache);
        var descriptorB = await ownerB.RegisterAsync(requestB, CancellationToken.None);
        var second = await ownerB.ReadAsync(descriptorB.Uri, CancellationToken.None);
        var downloadCountAfterAuthorizedCacheHit = files.DownloadCount;
        var otherUser = CreateGateway(db, files, credentials, user: new User("teacher-c"), contentCache: contentCache);
        var exception = await Assert.ThrowsAsync<MoodleResourceException>(
            () => otherUser.ReadAsync(descriptorB.Uri, CancellationToken.None));

        Assert.Equal(first.Content, second.Content);
        Assert.Equal("RESOURCE_FORBIDDEN", exception.ErrorCode);
        Assert.Equal(1, downloadCountAfterAuthorizedCacheHit);
        Assert.Equal(downloadCountAfterAuthorizedCacheHit, files.DownloadCount);
    }

    [Fact]
    public async Task ReadAsync_ConcurrentUnknownHashRequestsShareDownloadAndPersistHashBeforeCompleting()
    {
        await using var db = CreateDb();
        using var contentCache = new MoodleResourceContentCache(TimeProvider.System);
        var files = new BlockingFileGateway("synthetic concurrent bytes"u8.ToArray());
        var gateway = CreateGateway(
            db,
            files,
            new Credentials("client-a", "connection-a"),
            user: new User("teacher-a"),
            contentCache: contentCache);
        var descriptor = await gateway.RegisterAsync(
            new MoodleResourceRegistration(
                "submission_attachment", "synthetic.txt", "text/plain", "https://moodle.example/pluginfile.php/1/synthetic.txt"),
            CancellationToken.None);
        Assert.True(MoodleResourceUri.TryParse(descriptor.Uri, out var resourceId));

        var firstTask = gateway.ReadAsync(descriptor.Uri, CancellationToken.None);
        await files.Started.Task;
        var secondTask = gateway.ReadAsync(descriptor.Uri, CancellationToken.None);
        await WaitUntilAsync(() => contentCache.ActiveFlightWaiterCountForTesting(resourceId) == 2);
        files.Release.TrySetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, files.DownloadCount);
        Assert.Equal(results[0].Content, results[1].Content);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(files.Bytes)).ToLowerInvariant(),
            (await db.MoodleResources.SingleAsync()).Sha256);
    }

    [Fact]
    public async Task RegisterManyAsync_ReusesOnlyExactReferencesWithoutDuplicatingPage()
    {
        await using var db = CreateDb();
        var credentials = new Credentials("client-a", "connection-a");
        var gateway = CreateGateway(db, new FileGateway(), credentials, user: new User("teacher-a"));

        var firstRequest = new MoodleResourceRegistration(
            "submission_attachment",
            "a.txt",
            "text/plain",
            "https://moodle.example/pluginfile.php/1/a.txt",
            CourseId: 10,
            AssignmentId: 501,
            SubmissionId: 7001,
            StudentId: 9001);
        var first = await gateway.RegisterManyAsync([firstRequest], CancellationToken.None);

        var secondGateway = CreateGateway(db, new FileGateway(), credentials, user: new User("teacher-a"));
        var descriptors = await secondGateway.RegisterManyAsync(
            [
                firstRequest,
                firstRequest with
                {
                    Filename = "b.txt",
                    RemoteFileReference = "https://moodle.example/pluginfile.php/1/b.txt",
                    SubmissionId = 7002,
                    StudentId = 9002
                }
            ],
            CancellationToken.None);

        Assert.Equal(2, descriptors.Count);
        Assert.Equal(first[0].Uri, descriptors[0].Uri);
        Assert.NotEqual(descriptors[0].Uri, descriptors[1].Uri);
        Assert.Equal(2, await db.MoodleResources.CountAsync());
    }

    [Fact]
    public async Task RegisterAsync_NaoReutilizaReferenciaQuandoHashMudou()
    {
        await using var db = CreateDb();
        var credentials = new Credentials("client-a", "connection-a");
        var gateway = CreateGateway(db, new FileGateway(), credentials, user: new User("teacher-a"));
        var reference = "https://moodle.example/pluginfile.php/1/enunciado.pdf";
        var first = await gateway.RegisterAsync(
            new MoodleResourceRegistration(
                "assignment_context_attachment", "enunciado.pdf", "application/pdf", reference,
                CourseId: 10, AssignmentId: 501, Sha256: new string('a', 64)),
            CancellationToken.None);

        var changed = await gateway.RegisterAsync(
            new MoodleResourceRegistration(
                "assignment_context_attachment", "enunciado.pdf", "application/pdf", reference,
                CourseId: 10, AssignmentId: 501, Sha256: new string('b', 64)),
            CancellationToken.None);
        var same = await gateway.RegisterAsync(
            new MoodleResourceRegistration(
                "assignment_context_attachment", "enunciado.pdf", "application/pdf", reference,
                CourseId: 10, AssignmentId: 501, Sha256: new string('a', 64)),
            CancellationToken.None);

        Assert.NotEqual(first.Uri, changed.Uri);
        Assert.Equal(first.Uri, same.Uri);
        Assert.Equal(2, await db.MoodleResources.CountAsync());
    }

    [Fact]
    public async Task ReadAsync_DeliversBytesWithoutInspectingMimeOrSignature()
    {
        await using var db = CreateDb();
        var gateway = CreateGateway(db, new InvalidFileGateway(), new Credentials("client-a", "connection-a"));
        var descriptor = await gateway.RegisterAsync(new MoodleResourceRegistration("submission_attachment", "atividade.pdf", "application/pdf", "https://moodle.example/pluginfile.php/1/a.pdf"), CancellationToken.None);

        var result = await gateway.ReadAsync(descriptor.Uri, CancellationToken.None);

        Assert.Equal([1, 2, 3], result.Content);
        Assert.Equal("image/png", result.MimeType);
    }

    [Fact]
    public async Task ReadAsync_UsesDedicatedLimitForScormZipResources()
    {
        await using var db = CreateDb();
        var content = new byte[2 * 1024 * 1024];
        var gateway = CreateGateway(
            db,
            new TypedFileGateway("application/zip", content),
            new Credentials("client-a", "connection-a"),
            limits: new GradingLimitsOptions
            {
                MaxResourceBytes = 1,
                MaxScormPackageResourceBytes = 3 * 1024 * 1024
            });
        var descriptor = await gateway.RegisterAsync(
            new MoodleResourceRegistration(
                "scorm_package",
                "curso.zip",
                "application/zip",
                "https://moodle.example/webservice/pluginfile.php/1/curso.zip",
                SizeBytes: content.LongLength),
            CancellationToken.None);

        var result = await gateway.ReadAsync(descriptor.Uri, CancellationToken.None);

        Assert.Equal(content, result.Content);
        Assert.Equal(content.LongLength, result.SizeBytes);
    }

    [Theory]
    [InlineData("resposta.rtf", "text/rtf")]
    [InlineData("resposta.rtf", "application/octet-stream")]
    [InlineData("resposta.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("resposta.pdf", "application/pdf")]
    [InlineData("planilha.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task ReadAsync_PreservesOriginalFileForCommonSubmissionFormats(string filename, string mimeType)
    {
        await using var db = CreateDb();
        var original = System.Text.Encoding.UTF8.GetBytes($"bytes originais de {filename}");
        var gateway = CreateGateway(
            db,
            new TypedFileGateway(mimeType, original),
            new Credentials("client-a", "connection-a"));
        var descriptor = await gateway.RegisterAsync(
            new MoodleResourceRegistration(
                "submission_attachment",
                filename,
                mimeType,
                $"https://moodle.example/pluginfile.php/1/{filename}"),
            CancellationToken.None);

        var result = await gateway.ReadAsync(descriptor.Uri, CancellationToken.None);

        Assert.Equal(original, result.Content);
        Assert.Equal(mimeType, result.MimeType);
        Assert.Equal(filename, descriptor.Filename);
        Assert.Equal(mimeType, descriptor.MimeType);
    }

    [Fact]
    public async Task ReadAsync_WhenMoodleDownloadFails_DoesNotReturnContent()
    {
        await using var db = CreateDb();
        var gateway = CreateGateway(
            db,
            new FailingFileGateway(),
            new Credentials("client-a", "connection-a"));
        var descriptor = await gateway.RegisterAsync(
            new MoodleResourceRegistration(
                "submission_attachment",
                "resposta.rtf",
                "text/rtf",
                "https://moodle.example/pluginfile.php/1/resposta.rtf"),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<MoodleResourceException>(
            () => gateway.ReadAsync(descriptor.Uri, CancellationToken.None));

        Assert.Equal("RESOURCE_DOWNLOAD_FAILED", exception.ErrorCode);
    }

    [Fact]
    public async Task ExpandZipAsync_CreatesOpaqueChildResources()
    {
        await using var db = CreateDb();
        var gateway = CreateGateway(db, new ZipFileGateway(CreateZip("resposta.txt", "conteudo"u8.ToArray())), new Credentials("client-a", "connection-a"), zipEnabled: true);
        var parent = await gateway.RegisterAsync(new MoodleResourceRegistration("submission_attachment", "entrega.zip", "application/zip", "https://moodle.example/pluginfile.php/1/a.zip"), CancellationToken.None);

        var children = await gateway.ExpandZipAsync(parent.Uri, CancellationToken.None);

        var child = Assert.Single(children);
        Assert.Equal("resposta.txt", child.Filename);
        Assert.StartsWith("moodle://resource/", child.Uri, StringComparison.Ordinal);
        Assert.Equal("conteudo", System.Text.Encoding.UTF8.GetString((await gateway.ReadAsync(child.Uri, CancellationToken.None)).Content));
    }

    [Fact]
    public async Task ExpandZipAsync_BloqueiaPathTraversal()
    {
        await using var db = CreateDb();
        var gateway = CreateGateway(db, new ZipFileGateway(CreateZip("../escape.txt", "conteudo"u8.ToArray())), new Credentials("client-a", "connection-a"), zipEnabled: true);
        var parent = await gateway.RegisterAsync(new MoodleResourceRegistration("submission_attachment", "entrega.zip", "application/zip", "https://moodle.example/pluginfile.php/1/a.zip"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<MoodleResourceException>(() => gateway.ExpandZipAsync(parent.Uri, CancellationToken.None));

        Assert.Equal("RESOURCE_UNSUPPORTED", exception.ErrorCode);
    }

    private static ConnectorDbContext CreateDb() => new(new DbContextOptionsBuilder<ConnectorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    private static MoodleResourceGateway CreateGateway(ConnectorDbContext db, IMoodleSubmissionFileGateway files, IMoodleCredentialsProvider credentials, bool zipEnabled = false, ICurrentUserContext? user = null, MoodleResourceContentCache? contentCache = null, IMoodleConnectionSelection? selection = null, GradingLimitsOptions? limits = null) => new(new MoodleResourceRepository(db), files, credentials, selection ?? new FakeSelection(), user ?? new User(), Options.Create(limits ?? new GradingLimitsOptions()), Options.Create(new MoodleUniversalApiFeatureOptions { McpResourceSubmissionDeliveryEnabled = true, McpResourceZipEnabled = zipEnabled }), new Audits(), new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), contentCache ?? new MoodleResourceContentCache(TimeProvider.System));
    private static byte[] CreateZip(string name, byte[] content) { using var stream = new MemoryStream(); using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true)) { var entry = archive.CreateEntry(name); using var output = entry.Open(); output.Write(content); } return stream.ToArray(); }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Wait condition was not met.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class FileGateway : IMoodleSubmissionFileGateway
    {
        public byte[] Bytes { get; } = "%PDF-1.7"u8.ToArray();
        public int DownloadCount { get; private set; }
        public Task<SubmissionFileDownloadResult> DownloadFileAsync(string userExternalId, string fileUrl, string filename, long maxBytes, CancellationToken cancellationToken)
        {
            DownloadCount++;
            return Task.FromResult(new SubmissionFileDownloadResult(filename, filename.EndsWith(".pdf", StringComparison.Ordinal) ? "application/pdf" : "text/plain", Bytes.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Bytes)).ToLowerInvariant(), Bytes, false));
        }
    }
    private sealed class BlockingFileGateway(byte[] bytes) : IMoodleSubmissionFileGateway
    {
        public byte[] Bytes { get; } = bytes;
        public int DownloadCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SubmissionFileDownloadResult> DownloadFileAsync(
            string userExternalId,
            string fileUrl,
            string filename,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            DownloadCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new SubmissionFileDownloadResult(
                filename,
                "text/plain",
                Bytes.LongLength,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Bytes)).ToLowerInvariant(),
                Bytes,
                false);
        }
    }
    private sealed class Credentials(string clientId, string connectionId) : IMoodleCredentialsProvider { public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(new MoodleCredentials(clientId, connectionId, "default", "https://moodle.example", "u", "p", "default", false)); }
    private sealed class FakeSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private sealed class AliasCredentials(IMoodleConnectionSelection selection) : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken)
        {
            var isFieg = string.Equals(selection.Alias, "FIEG", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(isFieg
                ? new MoodleCredentials("client-a", "connection-fieg", "FIEG", "https://fieg.example", "u", "p", "fieg", false)
                : new MoodleCredentials("client-a", "connection-default", "default", "https://default.example", "u", "p", "default", false));
        }
    }
    private sealed class InvalidFileGateway : IMoodleSubmissionFileGateway { public Task<SubmissionFileDownloadResult> DownloadFileAsync(string userExternalId, string fileUrl, string filename, long maxBytes, CancellationToken cancellationToken) { byte[] content = [1, 2, 3]; return Task.FromResult(new SubmissionFileDownloadResult(filename, "image/png", content.LongLength, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant(), content, false)); } }
    private sealed class TypedFileGateway(string mimeType, byte[] bytes) : IMoodleSubmissionFileGateway
    {
        public Task<SubmissionFileDownloadResult> DownloadFileAsync(string userExternalId, string fileUrl, string filename, long maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(new SubmissionFileDownloadResult(
                filename,
                mimeType,
                bytes.LongLength,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes,
                false));
    }
    private sealed class FailingFileGateway : IMoodleSubmissionFileGateway
    {
        public Task<SubmissionFileDownloadResult> DownloadFileAsync(string userExternalId, string fileUrl, string filename, long maxBytes, CancellationToken cancellationToken) =>
            Task.FromException<SubmissionFileDownloadResult>(new InvalidOperationException("download failed"));
    }
    private sealed class ZipFileGateway(byte[] bytes) : IMoodleSubmissionFileGateway { public Task<SubmissionFileDownloadResult> DownloadFileAsync(string userExternalId, string fileUrl, string filename, long maxBytes, CancellationToken cancellationToken) => Task.FromResult(new SubmissionFileDownloadResult(filename, "application/zip", bytes.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), bytes, false)); }
    private sealed class User(string subject = "teacher", bool isAdmin = false) : ICurrentUserContext { public string Subject => subject; public string? Email => null; public IReadOnlyCollection<string> Scopes => isAdmin ? ["grading.admin"] : []; public bool HasScope(string scope) => isAdmin && string.Equals(scope, "grading.admin", StringComparison.Ordinal); }
    private sealed class Audits : IMoodleAuditLogRepository { public Task AddAsync(MoodleAuditLog log, CancellationToken cancellationToken) => Task.CompletedTask; public Task<int> CountByBatchJobIdAsync(Guid batchJobId, CancellationToken cancellationToken) => Task.FromResult(0); public Task<int> CountByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken) => Task.FromResult(0); public Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(Guid batchJobId, int page, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]); public Task<IReadOnlyList<MoodleAuditLog>> ListByCorrelationIdAsync(string correlationId, int page, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]); public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
}
