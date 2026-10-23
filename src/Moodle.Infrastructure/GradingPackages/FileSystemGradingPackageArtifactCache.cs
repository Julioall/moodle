using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;

namespace Moodle.Infrastructure;

/// <summary>
/// Cache de artifacts dentro do mesmo storage privado dos pacotes. A chave
/// inclui o ID do artifact e o hash verificado, portanto uma mudança de bytes
/// nunca reutiliza conteúdo antigo.
/// </summary>
public sealed class FileSystemGradingPackageArtifactCache : IGradingPackageArtifactCache
{
    private readonly string rootDirectory;
    private readonly IGradingPackageBlobStore blobStore;

    public FileSystemGradingPackageArtifactCache(
        IOptions<GradingLimitsOptions> options,
        IGradingPackageBlobStore blobStore)
    {
        var configured = options.Value.GradingPackageBlobRootDirectory;
        rootDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "private-grading-packages")
                : configured);
        this.blobStore = blobStore;
    }

    public async Task<GradingPackageArtifactCacheHit?> TryOpenReadAsync(
        Guid artifactId,
        string sha256,
        long? expectedSizeBytes,
        CancellationToken cancellationToken)
    {
        if (artifactId == Guid.Empty || !TryNormalizeHash(sha256, out var normalizedHash))
        {
            return null;
        }

        var key = BuildKey(artifactId, normalizedHash);
        Stream? input = null;
        try
        {
            input = await blobStore.OpenReadAsync(key, cancellationToken);
            var (sizeBytes, actualHash) = await ComputeHashAsync(input, cancellationToken);
            await input.DisposeAsync();
            input = null;

            if ((expectedSizeBytes is > 0 && expectedSizeBytes != sizeBytes) ||
                !string.Equals(actualHash, normalizedHash, StringComparison.OrdinalIgnoreCase))
            {
                await TryDeleteAsync(key, cancellationToken);
                return null;
            }

            var verified = await blobStore.OpenReadAsync(key, cancellationToken);
            return new GradingPackageArtifactCacheHit(verified, sizeBytes, actualHash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            if (input is not null)
            {
                await input.DisposeAsync();
            }
        }
    }

    public async Task StoreAsync(
        Guid artifactId,
        string sha256,
        long sizeBytes,
        Func<Stream, CancellationToken, Task> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (artifactId == Guid.Empty || !TryNormalizeHash(sha256, out var normalizedHash))
        {
            throw new ArgumentException("O artifact e o hash sao obrigatorios para o cache.");
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        var key = BuildKey(artifactId, normalizedHash);
        var descriptor = await blobStore.WriteAsync(key, writer, cancellationToken);
        if (descriptor.SizeBytes != sizeBytes ||
            !string.Equals(descriptor.Sha256, normalizedHash, StringComparison.OrdinalIgnoreCase))
        {
            await TryDeleteAsync(key, cancellationToken);
            throw new InvalidDataException("O artifact armazenado no cache nao corresponde ao hash esperado.");
        }
    }

    public Task<int> RemoveExpiredAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheDirectory = Path.Combine(rootDirectory, "artifacts");
        if (!Directory.Exists(cacheDirectory))
        {
            return Task.FromResult(0);
        }

        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTime lastWrite;
            try
            {
                lastWrite = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (lastWrite >= cutoff.UtcDateTime)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return Task.FromResult(removed);
    }

    private async Task TryDeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await blobStore.DeleteAsync(key, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string BuildKey(Guid artifactId, string normalizedHash) =>
        $"artifacts/{artifactId:N}/{normalizedHash}.bin";

    private static bool TryNormalizeHash(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            return false;
        }

        try
        {
            _ = Convert.FromHexString(value);
            normalized = value.ToLowerInvariant();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<(long SizeBytes, string Sha256)> ComputeHashAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var sizeBytes = 0L;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            sizeBytes += read;
        }

        return (sizeBytes, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
