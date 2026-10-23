namespace Moodle.Application.Abstractions;

public sealed record GradingPackageBlobDescriptor(
    string BlobKey,
    long SizeBytes,
    string Sha256);

/// <summary>
/// Armazenamento privado de bytes. A camada de aplicação autoriza o pacote e
/// o proprietário antes de chamar o store; o blob store nunca transforma um
/// caminho em uma URL pública.
/// </summary>
public interface IGradingPackageBlobStore
{
    Task<GradingPackageBlobDescriptor> WriteAsync(
        string blobKey,
        Func<Stream, CancellationToken, Task> writer,
        CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(
        string blobKey,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        string blobKey,
        CancellationToken cancellationToken);
}
