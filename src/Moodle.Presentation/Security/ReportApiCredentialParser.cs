using System.Text;
using Microsoft.AspNetCore.Http;

namespace Moodle.Presentation.Security;

public sealed record ReportApiCredentialResult(string? ApiKey, string? Error);

public static class ReportApiCredentialParser
{
    public const string BasicUsername = "excel-report";

    public static ReportApiCredentialResult Parse(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = authorization["Basic ".Length..].Trim();
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var separator = decoded.IndexOf(':');
                if (separator <= 0 ||
                    !string.Equals(decoded[..separator], BasicUsername, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(decoded[(separator + 1)..]))
                    return new(null, "invalid_basic_credentials");

                return new(decoded[(separator + 1)..], null);
            }
            catch (FormatException)
            {
                return new(null, "invalid_basic_credentials");
            }
        }

        var headerApiKey = request.Headers["X-Mcp-Api-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(headerApiKey)) return new(headerApiKey, null);

        var queryApiKey = request.Query["api_key"].ToString();
        return !string.IsNullOrWhiteSpace(queryApiKey)
            ? new(queryApiKey, null)
            : new(null, "missing_credentials");
    }
}
