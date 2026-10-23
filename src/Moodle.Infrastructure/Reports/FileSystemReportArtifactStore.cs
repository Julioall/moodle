using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure.Configuration;

namespace Moodle.Infrastructure.Reports;

public sealed class FileSystemReportArtifactStore : IReportArtifactStore
{
    private readonly string rootDirectory;

    public FileSystemReportArtifactStore(IOptions<ReportArtifactOptions> options)
    {
        var configuredRoot = options.Value.RootDirectory;
        if (!ReportArtifactOptions.IsValidRootDirectory(configuredRoot))
        {
            throw new OptionsValidationException(
                ReportArtifactOptions.SectionName,
                typeof(ReportArtifactOptions),
                ["Report artifact storage must resolve outside wwwroot."]);
        }

        rootDirectory = ReportArtifactOptions.GetFullRootDirectory(configuredRoot);
        Directory.CreateDirectory(rootDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                rootDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public async Task WriteAsync(string blobKey, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var finalPath = ResolvePath(blobKey);
        Directory.CreateDirectory(rootDirectory);
        var temporaryPath = Path.Combine(rootDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 128 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var output = new FileStream(temporaryPath, fileOptions))
            {
                await output.WriteAsync(content, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(blobKey);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The report artifact is unavailable.");
        }

        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string blobKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(blobKey);
        File.Delete(path);
        return Task.CompletedTask;
    }

    private string ResolvePath(string blobKey)
    {
        if (string.IsNullOrWhiteSpace(blobKey) ||
            !Guid.TryParseExact(blobKey, "N", out var parsed) ||
            parsed == Guid.Empty)
        {
            throw new ArgumentException("The report artifact key is invalid.", nameof(blobKey));
        }

        var path = Path.GetFullPath(Path.Combine(rootDirectory, $"{parsed:N}.bin"));
        var rootPrefix = Path.TrimEndingDirectorySeparator(rootDirectory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The report artifact key is outside private storage.", nameof(blobKey));
        }

        return path;
    }
}
