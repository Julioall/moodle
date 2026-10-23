namespace Moodle.Application.Abstractions;

public interface IMoodleCredentialValidator
{
    Task<bool> ValidateAsync(string moodleBaseUrl, string username, string password, CancellationToken cancellationToken);
}
