using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure.MoodleApi;

internal sealed class MoodleResourceResolver : IMoodleResourceResolver
{
    public MoodleResourceReference Resolve(string input)
    {
        var original = input ?? string.Empty;
        var value = original.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return new MoodleResourceReference(MoodleResourceType.Unknown, original, string.Empty);
        }

        if (TryReadQuery(value, "categoryid", out var categoryId))
        {
            return new MoodleResourceReference(
                value.Contains("/course/index.php", StringComparison.OrdinalIgnoreCase) ? MoodleResourceType.CategoryUrl : MoodleResourceType.CategoryId,
                original,
                categoryId);
        }
        if (TryReadQuery(value, "id", out var courseId) && value.Contains("/course/view.php", StringComparison.OrdinalIgnoreCase))
        {
            return new MoodleResourceReference(MoodleResourceType.CourseUrl, original, courseId);
        }
        if (TryReadQuery(value, "search", out var search) && value.Contains("/course/search", StringComparison.OrdinalIgnoreCase))
        {
            return new MoodleResourceReference(MoodleResourceType.SearchUrl, original, search);
        }
        if (value.StartsWith("categoryid=", StringComparison.OrdinalIgnoreCase))
        {
            return new MoodleResourceReference(MoodleResourceType.CategoryId, original, value["categoryid=".Length..].Trim());
        }
        if (value.StartsWith("idnumber:", StringComparison.OrdinalIgnoreCase))
        {
            return new MoodleResourceReference(MoodleResourceType.IdNumber, original, value["idnumber:".Length..].Trim());
        }
        if (value.StartsWith("shortname:", StringComparison.OrdinalIgnoreCase))
        {
            return new MoodleResourceReference(MoodleResourceType.ShortName, original, value["shortname:".Length..].Trim());
        }
        if (long.TryParse(value, out _))
        {
            return new MoodleResourceReference(MoodleResourceType.CourseId, original, value);
        }

        return new MoodleResourceReference(MoodleResourceType.SearchTerm, original, value);
    }

    private static bool TryReadQuery(string input, string name, out string value)
    {
        value = string.Empty;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var marker = name + "=";
            var index = input.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            value = input[(index + marker.Length)..].Split('&', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            return !string.IsNullOrWhiteSpace(value);
        }

        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || !string.Equals(part[..separator], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = Uri.UnescapeDataString(part[(separator + 1)..]).Trim();
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }
}
