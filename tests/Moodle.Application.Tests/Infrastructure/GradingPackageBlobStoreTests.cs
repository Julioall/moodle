using System.IO.Compression;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading.Packages;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class GradingPackageBlobStoreTests
{
    [Fact]
    public async Task WriteAsync_WithRealArchiveWriter_PublishesReadableZip()
    {
        var root = Path.Combine(Path.GetTempPath(), "moodle-grading-package-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSystemGradingPackageBlobStore(
                Options.Create(new GradingLimitsOptions { GradingPackageBlobRootDirectory = root }));
            var writer = new GradingPackageArchiveWriter();
            var bytes = "evidencia controlada"u8.ToArray();
            var descriptor = await store.WriteAsync(
                "packages/diagnostic/package.zip",
                async (destination, token) => await writer.WriteAsync(
                    destination,
                    [new GradingPackageArchiveEntry(
                        "entregas/evidencia.txt",
                        (output, ct) => output.WriteAsync(bytes, ct).AsTask(),
                        bytes.LongLength)],
                    new GradingPackageArchiveLimits(10, 4096, 4096, 4096),
                    token),
                CancellationToken.None);

            await using var read = await store.OpenReadAsync(descriptor.BlobKey, CancellationToken.None);
            using var archive = new ZipArchive(read, ZipArchiveMode.Read);
            var entry = Assert.Single(archive.Entries);
            Assert.Equal("entregas/evidencia.txt", entry.FullName);
            using var reader = new StreamReader(entry.Open());
            Assert.Equal("evidencia controlada", await reader.ReadToEndAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WriteAsync_PublishesAtomicallyAndReadsBackBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "moodle-grading-package-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSystemGradingPackageBlobStore(
                Options.Create(new GradingLimitsOptions { GradingPackageBlobRootDirectory = root }));
            var expected = "conteudo do pacote"u8.ToArray();

            var descriptor = await store.WriteAsync(
                "packages/package-a/package.zip",
                (destination, _) => destination.WriteAsync(expected).AsTask(),
                CancellationToken.None);

            await using var read = await store.OpenReadAsync(descriptor.BlobKey, CancellationToken.None);
            using var buffer = new MemoryStream();
            await read.CopyToAsync(buffer);
            Assert.Equal(expected, buffer.ToArray());
            Assert.Equal(expected.LongLength, descriptor.SizeBytes);
            Assert.Equal(64, descriptor.Sha256.Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Store_RejectsTraversalKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "moodle-grading-package-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSystemGradingPackageBlobStore(
                Options.Create(new GradingLimitsOptions { GradingPackageBlobRootDirectory = root }));

            await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(
                "../outside.zip",
                (_, _) => Task.CompletedTask,
                CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ArtifactCache_reuses_only_verified_bytes_and_removes_corruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "moodle-grading-package-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new GradingLimitsOptions { GradingPackageBlobRootDirectory = root });
            var blobStore = new FileSystemGradingPackageBlobStore(options);
            var cache = new FileSystemGradingPackageArtifactCache(options, blobStore);
            var artifactId = Guid.NewGuid();
            var bytes = "artifacto em cache"u8.ToArray();
            var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            await cache.StoreAsync(
                artifactId,
                sha256,
                bytes.LongLength,
                (destination, _) => destination.WriteAsync(bytes).AsTask(),
                CancellationToken.None);

            var hit = await cache.TryOpenReadAsync(
                artifactId,
                sha256,
                bytes.LongLength,
                CancellationToken.None);
            Assert.NotNull(hit);
            await using (hit!.Content)
            {
                using var buffer = new MemoryStream();
                await hit.Content.CopyToAsync(buffer);
                Assert.Equal(bytes, buffer.ToArray());
            }

            var path = Path.Combine(root, "artifacts", artifactId.ToString("N"), $"{sha256}.bin");
            await File.WriteAllTextAsync(path, "conteudo corrompido");
            Assert.Null(await cache.TryOpenReadAsync(
                artifactId,
                sha256,
                bytes.LongLength,
                CancellationToken.None));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ArtifactCache_removes_entries_older_than_cutoff()
    {
        var root = Path.Combine(Path.GetTempPath(), "moodle-grading-package-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new GradingLimitsOptions { GradingPackageBlobRootDirectory = root });
            var blobStore = new FileSystemGradingPackageBlobStore(options);
            var cache = new FileSystemGradingPackageArtifactCache(options, blobStore);
            var artifactId = Guid.NewGuid();
            var bytes = "cache antigo"u8.ToArray();
            var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            await cache.StoreAsync(
                artifactId,
                sha256,
                bytes.LongLength,
                (destination, _) => destination.WriteAsync(bytes).AsTask(),
                CancellationToken.None);

            var path = Path.Combine(root, "artifacts", artifactId.ToString("N"), $"{sha256}.bin");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));

            var removed = await cache.RemoveExpiredAsync(
                DateTimeOffset.UtcNow.AddDays(-1),
                CancellationToken.None);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
