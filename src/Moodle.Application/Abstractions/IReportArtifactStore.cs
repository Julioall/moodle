namespace Moodle.Application.Abstractions;

public interface IReportArtifactStore
{
    Task WriteAsync(string blobKey, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken);

    Task DeleteAsync(string blobKey, CancellationToken cancellationToken);
}
