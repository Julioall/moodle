namespace Moodle.Application.Grading;

/// <summary>
/// Normaliza referências Moodle persistidas para que o worker possa recuperá-las
/// sem guardar tokens, query strings assinadas ou outras informações transitórias.
/// </summary>
internal static class GradingArtifactSourceReference
{
    public static string? Normalize(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl) ||
            !Uri.TryCreate(sourceUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }
}
