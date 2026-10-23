namespace Moodle.Application.Abstractions;

public interface ICurrentUserContext
{
    string Subject { get; }

    string? Email { get; }

    IReadOnlyCollection<string> Scopes { get; }

    bool HasScope(string scope);

    bool HasPlatformPermission(string permission) => false;
}
