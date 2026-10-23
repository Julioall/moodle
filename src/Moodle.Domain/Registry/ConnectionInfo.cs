namespace Moodle.Domain.Registry;

public sealed record ConnectionInfo(
    Guid ConnectionId,
    string Alias,
    string BaseUrl
);
