using System.IO.Compression;
using System.Text;
using Moodle.Application.Grading.Packages;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class GradingPackageArchiveWriterTests
{
    [Fact]
    public async Task WriteAsync_RejectsPathTraversalAndAbsolutePaths()
    {
        var writer = new GradingPackageArchiveWriter();
        var entries = new[]
        {
            new GradingPackageArchiveEntry(
                "../escape.txt",
                (destination, _) => destination.WriteAsync("nao"u8.ToArray()).AsTask())
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(
            new MemoryStream(),
            entries,
            new GradingPackageArchiveLimits(10, 1024, 4096, 1024),
            CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_EnforcesEntryAndUncompressedLimitsAndProducesReadableZip()
    {
        var writer = new GradingPackageArchiveWriter();
        var destination = new MemoryStream();
        var entries = new[]
        {
            Entry("a.txt", "abc"),
            Entry("b.txt", "def")
        };

        var result = await writer.WriteAsync(
            destination,
            entries,
            new GradingPackageArchiveLimits(2, 6, 4096, 10),
            CancellationToken.None);

        Assert.Equal(2, result.EntryCount);
        Assert.Equal(6, result.UncompressedSizeBytes);
        destination.Position = 0;
        using var archive = new ZipArchive(destination, ZipArchiveMode.Read);
        Assert.Equal(["a.txt", "b.txt"], archive.Entries.Select(entry => entry.FullName).ToArray());
    }

    [Fact]
    public async Task WriteAsync_RejectsEntryLimitBeforeWritingMoreBytes()
    {
        var writer = new GradingPackageArchiveWriter();
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(
            new MemoryStream(),
            [Entry("too-large.txt", new string('x', 11))],
            new GradingPackageArchiveLimits(10, 100, 4096, 10),
            CancellationToken.None));
    }

    private static GradingPackageArchiveEntry Entry(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new GradingPackageArchiveEntry(
            path,
            (destination, _) => destination.WriteAsync(bytes).AsTask(),
            bytes.LongLength);
    }
}
