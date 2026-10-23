using System.Text.Json;

namespace Moodle.Application.Abstractions;

public interface IMoodleRestClient
{
    // No compatibility fallback: an unsupported limited read must fail, never silently retry.
    Task<JsonElement> CallReadOnceAsync(MoodleCredentials connection, string functionName,
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Single-attempt reads are not supported by this transport.");
    Task<JsonElement> CallAsync(
        MoodleCredentials connection,
        string functionName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken);

    Task<JsonElement> CallAsync(
        MoodleCredentials connection,
        string functionName,
        IReadOnlyDictionary<string, object?> parameters,
        bool allowServiceToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes a controlled write whose Moodle REST implementation may return
    /// an empty HTTP body on success. Read calls must keep treating an empty
    /// body as an invalid response.
    /// </summary>
    Task<JsonElement> CallWriteAsync(
        MoodleCredentials connection,
        string functionName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken) =>
        CallAsync(connection, functionName, parameters, allowServiceToken: false, cancellationToken);
}
