namespace Moodle.Infrastructure;

internal interface IMoodleEndpointValidator
{
    Task<Uri> ValidateAsync(string baseUrl, CancellationToken cancellationToken);

    Task<Uri> ValidateAsync(string baseUrl, CancellationToken cancellationToken, string auditId) =>
        ValidateAsync(baseUrl, cancellationToken);
}
