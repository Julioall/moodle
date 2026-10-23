using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace Moodle.Infrastructure.Scorm;

/// <summary>
/// Reuses bounded text reads of the same validated archive entry during one
/// package parse. File-only entries are read once without being retained.
/// </summary>
internal sealed class ScormEntryTextCache(Func<ZipArchiveEntry, int, CancellationToken, string> readEntryText)
{
    private readonly Dictionary<EntryKey, CachedText> _cached = new(EntryKeyComparer.Instance);

    public string ReadForReuse(string normalizedPath, ZipArchiveEntry entry, int maxChars, CancellationToken cancellationToken)
    {
        var key = new EntryKey(normalizedPath, entry);
        if (_cached.TryGetValue(key, out var cached) && cached.ReadLimit >= maxChars)
        {
            return Truncate(cached.Text, maxChars);
        }

        var text = readEntryText(entry, maxChars, cancellationToken);
        _cached[key] = new CachedText(text, maxChars);
        return text;
    }

    public string ReadForFile(string normalizedPath, ZipArchiveEntry entry, int maxChars, CancellationToken cancellationToken)
    {
        var key = new EntryKey(normalizedPath, entry);
        if (_cached.TryGetValue(key, out var cached) && cached.ReadLimit >= maxChars)
        {
            return Truncate(cached.Text, maxChars);
        }

        return readEntryText(entry, maxChars, cancellationToken);
    }

    public void Release(string normalizedPath, ZipArchiveEntry entry) =>
        _cached.Remove(new EntryKey(normalizedPath, entry));

    public void Clear() => _cached.Clear();

    private static string Truncate(string text, int maxChars) =>
        text.Length > maxChars ? text[..maxChars] : text;

    private readonly record struct EntryKey(string NormalizedPath, ZipArchiveEntry Entry);
    private sealed record CachedText(string Text, int ReadLimit);

    private sealed class EntryKeyComparer : IEqualityComparer<EntryKey>
    {
        public static EntryKeyComparer Instance { get; } = new();

        public bool Equals(EntryKey left, EntryKey right) =>
            ReferenceEquals(left.Entry, right.Entry) &&
            StringComparer.OrdinalIgnoreCase.Equals(left.NormalizedPath, right.NormalizedPath);

        public int GetHashCode(EntryKey key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.NormalizedPath), RuntimeHelpers.GetHashCode(key.Entry));
    }
}
