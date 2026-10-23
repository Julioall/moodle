namespace Moodle.Application.MoodleApi;

public static class MoodleResourceUri
{
    public const string Scheme = "moodle";
    public const string Authority = "resource";

    public static string Create(string resourceId)
    {
        if (!IsOpaqueId(resourceId)) throw new ArgumentException("Resource id invalido.", nameof(resourceId));
        return $"{Scheme}://{Authority}/{resourceId}";
    }

    public static bool TryParse(string? value, out string resourceId)
    {
        resourceId = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, Authority, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var candidate = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (!IsOpaqueId(candidate)) return false;
        resourceId = candidate;
        return true;
    }

    private static bool IsOpaqueId(string? value) => value?.Length == 32 && value.All(char.IsAsciiHexDigit);
}
