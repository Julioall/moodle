using Microsoft.Extensions.Options;
using Moodle.Infrastructure.Configuration;
using Moodle.Infrastructure.Reports;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class FileSystemReportArtifactStoreTests
{
    [Fact]
    public async Task WriteAsyncPromotesCompleteFileAndOpenReadStreamsContents()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var blobKey = Guid.NewGuid().ToString("N");
        var content = new byte[] { 11, 22, 33, 44 };

        await store.WriteAsync(blobKey, content, CancellationToken.None);

        var artifactPath = Path.Combine(directory.Path, $"{blobKey}.bin");
        Assert.True(File.Exists(artifactPath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        await using var stream = await store.OpenReadAsync(blobKey, CancellationToken.None);
        Assert.IsType<FileStream>(stream);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        Assert.Equal(content, buffer.ToArray());
    }

    [Fact]
    public async Task DeleteAsyncIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var blobKey = Guid.NewGuid().ToString("N");
        await store.WriteAsync(blobKey, new byte[] { 1, 2, 3 }, CancellationToken.None);

        await store.DeleteAsync(blobKey, CancellationToken.None);
        await store.DeleteAsync(blobKey, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(directory.Path, $"{blobKey}.bin")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:\\outside")]
    [InlineData("not-a-guid")]
    public async Task RejectsPathTraversalKeys(string blobKey)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(blobKey, new byte[] { 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync(blobKey, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(blobKey, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledWriteRemovesTempAndKeepsPublishedArtifact()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var blobKey = Guid.NewGuid().ToString("N");
        var original = new byte[] { 4, 5, 6 };
        await store.WriteAsync(blobKey, original, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync(blobKey, new byte[] { 7, 8, 9 }, cancellation.Token));

        await using var stream = await store.OpenReadAsync(blobKey, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        Assert.Equal(original, buffer.ToArray());
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void RootUnderWebRootIsRejected()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var artifactRoot = Path.Combine(webRoot, "private-report-artifacts");

        Assert.Throws<OptionsValidationException>(() => CreateStore(artifactRoot));
    }

    [Fact]
    public async Task PrivateStorageUsesOwnerOnlyUnixPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var blobKey = Guid.NewGuid().ToString("N");
        await store.WriteAsync(blobKey, new byte[] { 1, 2, 3 }, CancellationToken.None);

        var privateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var privateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var permissionMask = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        Assert.Equal(privateDirectoryMode, File.GetUnixFileMode(directory.Path) & permissionMask);
        Assert.Equal(privateFileMode, File.GetUnixFileMode(Path.Combine(directory.Path, $"{blobKey}.bin")) & permissionMask);
    }

    private static FileSystemReportArtifactStore CreateStore(string rootDirectory) =>
        new(Options.Create(new ReportArtifactOptions { RootDirectory = rootDirectory }));

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"report-artifacts-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
