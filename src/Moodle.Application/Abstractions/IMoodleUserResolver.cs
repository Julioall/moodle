namespace Moodle.Application.Abstractions;

public interface IMoodleUserResolver
{
    Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken);
}
