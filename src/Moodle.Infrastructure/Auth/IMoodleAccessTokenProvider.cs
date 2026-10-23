using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

internal interface IMoodleAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(
        MoodleCredentials connection,
        CancellationToken cancellationToken);

    Task<string> GetAccessTokenAsync(
        MoodleCredentials connection,
        CancellationToken cancellationToken,
        string auditId) => GetAccessTokenAsync(connection, cancellationToken);

    void Invalidate(MoodleCredentials connection);
}
