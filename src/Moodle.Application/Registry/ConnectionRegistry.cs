using Moodle.Application.Abstractions;
using Moodle.Domain.Registry;
using System.Security.Cryptography;
using System.Text;

namespace Moodle.Application.Registry;

public sealed class ConnectionRegistry : IConnectionRegistry
{
    private readonly IMoodleConnectionSelection _moodleSelection;
    private readonly IMoodleCredentialsProvider _credentialsProvider;

    public ConnectionRegistry(
        IMoodleConnectionSelection moodleSelection,
        IMoodleCredentialsProvider credentialsProvider)
    {
        _moodleSelection = moodleSelection;
        _credentialsProvider = credentialsProvider;
    }

    public async Task<ConnectionInfo?> ResolveConnectionAsync(
        string? alias,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(alias))
        {
            _moodleSelection.Alias = alias.Trim();
        }

        var credentials = await _credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var connectionId = ToDeterministicGuid(credentials.ConnectionId);
        return new ConnectionInfo(
            connectionId,
            MoodleConnectionAlias.NormalizeOrDefault(credentials.Alias),
            credentials.BaseUrl);
    }

    private static Guid ToDeterministicGuid(string connectionId)
    {
        if (Guid.TryParse(connectionId, out var parsed))
        {
            return parsed;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(connectionId ?? string.Empty));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
