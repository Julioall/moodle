using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;

namespace Moodle.Infrastructure;

public sealed class FileSystemGradingPackageBlobStore : IGradingPackageBlobStore
{
    private readonly string rootDirectory;

    public FileSystemGradingPackageBlobStore(IOptions<GradingLimitsOptions> options)
    {
        var configured = options.Value.GradingPackageBlobRootDirectory;
        rootDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "private-grading-packages")
                : configured);
        Directory.CreateDirectory(rootDirectory);
    }

    public async Task<GradingPackageBlobDescriptor> WriteAsync(
        string blobKey,
        Func<Stream, CancellationToken, Task> writer,
        CancellationToken cancellationToken)
    {
        var finalPath = ResolvePath(blobKey);
        ArgumentNullException.ThrowIfNull(writer);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var temporaryPath = finalPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                // O gerador relê o ZIP para validá-lo antes da promoção atômica.
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await writer(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            var (size, sha256) = await ComputeHashAsync(temporaryPath, cancellationToken);
            File.Move(temporaryPath, finalPath, overwrite: true);
            return new GradingPackageBlobDescriptor(blobKey, size, sha256);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<Stream> OpenReadAsync(
        string blobKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(blobKey);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("O pacote de correcao nao esta disponivel.", path);
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

    public Task DeleteAsync(
        string blobKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(blobKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string ResolvePath(string blobKey)
    {
        if (string.IsNullOrWhiteSpace(blobKey) ||
            blobKey.Contains('\0') ||
            Path.IsPathRooted(blobKey))
        {
            throw new ArgumentException("A chave do blob e invalida.", nameof(blobKey));
        }

        var normalized = blobKey.Replace('\\', '/');
        if (normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("A chave do blob e invalida.", nameof(blobKey));
        }
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new ArgumentException("A chave do blob e invalida.", nameof(blobKey));
        }

        var root = Path.GetFullPath(rootDirectory + Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(rootDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A chave do blob sai do diretorio privado.", nameof(blobKey));
        }

        return fullPath;
    }

    private static async Task<(long Size, string Sha256)> ComputeHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var total = 0L;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
