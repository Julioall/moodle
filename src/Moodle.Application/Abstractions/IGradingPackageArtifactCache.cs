namespace Moodle.Application.Abstractions;

public sealed record GradingPackageArtifactCacheHit(
    Stream Content,
    long SizeBytes,
    string Sha256);

/// <summary>
/// Cache privado e opcional de artifacts já verificados. Ele é uma otimização
/// de retomada: ausência, corrupção ou indisponibilidade do cache nunca deve
/// impedir um novo download pelo worker.
/// </summary>
public interface IGradingPackageArtifactCache
{
    Task<GradingPackageArtifactCacheHit?> TryOpenReadAsync(
        Guid artifactId,
        string sha256,
        long? expectedSizeBytes,
        CancellationToken cancellationToken);

    Task StoreAsync(
        Guid artifactId,
        string sha256,
        long sizeBytes,
        Func<Stream, CancellationToken, Task> writer,
        CancellationToken cancellationToken);

    Task<int> RemoveExpiredAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken);
}
