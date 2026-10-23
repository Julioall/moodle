namespace Moodle.Domain;

public static class MoodleDownloadFilename
{
    public static string Resolve(string? requested, string? responseName, Uri? source)
    {
        foreach (var candidate in new[] { requested, responseName, SourceBasename(source) })
        {
            var safe = Sanitize(candidate);
            if (!string.IsNullOrEmpty(safe)) return safe;
        }
        return "arquivo";
    }

    public static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // Both separators and Windows-invalid characters matter even on Linux.
        var basename = name.Trim().Trim('"').Replace('\\', '/').Split('/').Last();
        var safe = new string(basename.Where(c => !char.IsControl(c) && "<>:\"/\\|?*".IndexOf(c) < 0).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..") return null;
        var stem = safe.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9'))
            safe = "_" + safe;
        if (safe.Length > 180)
        {
            var extension = Path.GetExtension(safe);
            if (extension.Length > 16) extension = string.Empty;
            var length = 180 - extension.Length;
            if (char.IsHighSurrogate(safe[length - 1])) length--;
            safe = safe[..length] + extension;
        }
        return safe;
    }

    private static string? SourceBasename(Uri? source)
    {
        if (source is null || source.AbsolutePath.EndsWith('/')) return null;
        var last = source.AbsolutePath.Split('/').Last();
        // Endpoint-only URLs do not identify a file; never use an authenticated query.
        if (last.Equals("pluginfile.php", StringComparison.OrdinalIgnoreCase)) return null;
        return Uri.UnescapeDataString(last);
    }
}
