namespace Moodle.Application.Abstractions;

public sealed record MoodleResourceRegistration(
    string ResourceType,
    string Filename,
    string MimeType,
    string RemoteFileReference,
    long? CourseId = null,
    long? AssignmentId = null,
    long? SubmissionId = null,
    long? StudentId = null,
    string? ContextId = null,
    string? Component = null,
    string? FileArea = null,
    string? ItemId = null,
    long? SizeBytes = null,
    string? Sha256 = null);

public sealed record MoodleResourceDescriptor(string Uri, string Filename, string MimeType, long? SizeBytes, string? Sha256);

public sealed record MoodleResourceReadResult(string Uri, string MimeType, byte[] Content, long SizeBytes, string Sha256);

public interface IMoodleResourceGateway
{
    Task<MoodleResourceDescriptor> RegisterAsync(MoodleResourceRegistration request, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a page of resources with one persistence flush. The default
    /// keeps legacy gateways source-compatible; production gateways override
    /// it to avoid one database transaction per student attachment.
    /// Results preserve the input order.
    /// </summary>
    async Task<IReadOnlyList<MoodleResourceDescriptor>> RegisterManyAsync(
        IReadOnlyList<MoodleResourceRegistration> requests,
        CancellationToken cancellationToken)
    {
        var result = new List<MoodleResourceDescriptor>(requests.Count);
        foreach (var request in requests)
        {
            result.Add(await RegisterAsync(request, cancellationToken));
        }

        return result;
    }

    Task<MoodleResourceReadResult> ReadAsync(string uri, CancellationToken cancellationToken);
    Task<IReadOnlyList<MoodleResourceDescriptor>> ExpandZipAsync(string uri, CancellationToken cancellationToken);
}
