using System.IO.Compression;
using Moodle.Application.Grading.Packages;

namespace Moodle.Infrastructure;

public sealed class GradingPackageArchiveWriter : IGradingPackageArchiveWriter
{
    public async Task<GradingPackageArchiveResult> WriteAsync(
        Stream destination,
        IReadOnlyCollection<GradingPackageArchiveEntry> entries,
        GradingPackageArchiveLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(entries);
        if (!destination.CanWrite || (limits.MaxEntries <= 0 || limits.MaxUncompressedBytes <= 0 ||
                                      limits.MaxArchiveBytes <= 0 || limits.MaxEntryBytes <= 0))
        {
            throw new ArgumentException("O destino ou os limites do pacote sao invalidos.", nameof(destination));
        }

        if (entries.Count > limits.MaxEntries)
        {
            throw new InvalidDataException("O pacote excede a quantidade maxima de entradas.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var uncompressedBytes = 0L;
        var writtenEntries = 0;
        var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedPath = NormalizePath(entry.Path);
                if (!paths.Add(normalizedPath))
                {
                    throw new InvalidDataException($"A entrada {normalizedPath} foi repetida no pacote.");
                }

                if (entry.DeclaredSizeBytes is long declaredSize &&
                    (declaredSize < 0 || declaredSize > limits.MaxEntryBytes))
                {
                    throw new InvalidDataException($"A entrada {normalizedPath} excede o limite de bytes.");
                }

                var zipEntry = archive.CreateEntry(normalizedPath, CompressionLevel.Optimal);
                await using var rawOutput = zipEntry.Open();
                await using var countedOutput = new CountingWriteStream(
                    rawOutput,
                    limits.MaxEntryBytes,
                    () => uncompressedBytes);
                await entry.WriteContentAsync(countedOutput, cancellationToken);
                await countedOutput.FlushAsync(cancellationToken);
                uncompressedBytes += countedOutput.BytesWritten;
                if (uncompressedBytes > limits.MaxUncompressedBytes)
                {
                    throw new InvalidDataException("O pacote excede o limite de bytes descompactados.");
                }

                writtenEntries++;
                if (destination.CanSeek && destination.Length > limits.MaxArchiveBytes)
                {
                    throw new InvalidDataException("O pacote excede o limite de bytes compactados.");
                }
            }
        }
        finally
        {
            archive.Dispose();
        }

        if (destination.CanSeek && destination.Length > limits.MaxArchiveBytes)
        {
            throw new InvalidDataException("O pacote excede o limite de bytes compactados.");
        }

        if (destination.CanSeek)
        {
            await ValidateFinalArchiveAsync(destination, writtenEntries, limits, cancellationToken);
        }

        return new GradingPackageArchiveResult(writtenEntries, uncompressedBytes);
    }

    private static async Task ValidateFinalArchiveAsync(
        Stream destination,
        int expectedEntryCount,
        GradingPackageArchiveLimits limits,
        CancellationToken cancellationToken)
    {
        var originalPosition = destination.Position;
        destination.Position = 0;
        try
        {
            using var archive = new ZipArchive(destination, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count != expectedEntryCount || archive.Entries.Count > limits.MaxEntries)
            {
                throw new InvalidDataException("O ZIP finalizado possui cobertura de entradas inconsistente.");
            }

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NormalizePath(entry.FullName);
            }
        }
        finally
        {
            destination.Position = originalPosition;
        }

        await Task.CompletedTask;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("Uma entrada do pacote possui caminho vazio.");
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(':') ||
            normalized.Contains('\0'))
        {
            throw new InvalidDataException($"O caminho da entrada {path} nao e seguro.");
        }

        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException($"O caminho da entrada {path} nao e seguro.");
        }

        return normalized;
    }

    private sealed class CountingWriteStream(
        Stream inner,
        long maxBytes,
        Func<long> packageBytes) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken);
            BytesWritten += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Flush();
            }

            base.Dispose(disposing);
        }

        private void EnsureCapacity(int count)
        {
            if (count < 0 || BytesWritten > maxBytes - count || packageBytes() > long.MaxValue - count)
            {
                throw new InvalidDataException("Uma entrada do pacote excede o limite de bytes.");
            }
        }
    }
}
