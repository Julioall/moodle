using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Moodle.Application.Pedagogy;

namespace Moodle.Infrastructure.Pedagogy;

public sealed partial class MarkdownPedagogicGuidanceSearch : IPedagogicGuidanceSearch
{
    private const int MaximumBlockLength = 1600;
    private const int MaximumExcerptLength = 400;
    private const int MaximumQueryLength = 300;
    private static readonly string[] EditorialTerms = ["capa", "creditos", "autoria", "autor", "autores", "expediente", "ficha tecnica", "copyright"];
    private readonly IndexedBlock[] _index;

    public MarkdownPedagogicGuidanceSearch(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        cancellationToken.ThrowIfCancellationRequested();
        _index = BuildIndex(Path.GetFullPath(rootPath), cancellationToken);
    }

    public Task<IReadOnlyList<PedagogicGuidanceSearchResult>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var terms = Normalize(TruncateQuery((query ?? string.Empty).Trim()))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (terms.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<PedagogicGuidanceSearchResult>>([]);
        }

        var sections = new Dictionary<SectionKey, SectionMatch>();
        foreach (var block in _index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new SectionKey(block.RelativePath, block.SectionOrdinal);
            if (!sections.TryGetValue(key, out var match))
            {
                match = new SectionMatch(block, terms);
                sections.Add(key, match);
            }

            match.Add(block, terms, cancellationToken);
        }

        IReadOnlyList<PedagogicGuidanceSearchResult> results = sections.Values
            .Where(match => match.MatchedTerms.Any(found => found))
            .Select(match => match.ToResult(terms))
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.RelativePath, StringComparer.Ordinal)
            .ThenBy(result => result.Section, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 10))
            .ToArray();
        return Task.FromResult(results);
    }

    private static IndexedBlock[] BuildIndex(string rootPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootPath))
        {
            return [];
        }

        var index = new List<IndexedBlock>();
        foreach (var path in Directory.EnumerateFiles(rootPath, "*.md", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IndexFile(path, Path.GetRelativePath(rootPath, path).Replace('\\', '/'), index, cancellationToken);
        }

        return index.ToArray();
    }

    private static void IndexFile(
        string path,
        string relativePath,
        List<IndexedBlock> index,
        CancellationToken cancellationToken)
    {
        var title = string.Empty;
        var section = string.Empty;
        var sectionOrdinal = 0;
        var buffer = new StringBuilder(MaximumBlockLength);
        var sectionHasBlock = false;

        void AddBlock(bool includeEmpty)
        {
            if (buffer.Length == 0 && !includeEmpty)
            {
                return;
            }

            var body = buffer.ToString().TrimEnd();
            buffer.Clear();
            index.Add(new IndexedBlock(
                title,
                section.Length > 0 ? section : title,
                relativePath,
                sectionOrdinal,
                body,
                Normalize(title),
                Normalize(section.Length > 0 ? section : title),
                Normalize(body)));
            sectionHasBlock = true;
        }

        void Append(string value)
        {
            var offset = 0;
            while (offset < value.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var available = MaximumBlockLength - buffer.Length;
                var length = Math.Min(available, value.Length - offset);
                if (length > 0 && offset + length < value.Length && char.IsHighSurrogate(value[offset + length - 1]))
                {
                    length--;
                }

                buffer.Append(value, offset, length);
                offset += length;
                if (buffer.Length == MaximumBlockLength || length == 0)
                {
                    AddBlock(includeEmpty: false);
                }
            }
        }

        foreach (var line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var heading = HeadingRegex().Match(line);
            if (!heading.Success)
            {
                Append(line);
                Append("\n");
                continue;
            }

            AddBlock(includeEmpty: false);
            var headingText = heading.Groups[2].Value.Trim();
            if (title.Length == 0)
            {
                title = headingText;
            }

            section = headingText;
            sectionOrdinal++;
            sectionHasBlock = false;
        }

        AddBlock(includeEmpty: section.Length > 0 && !sectionHasBlock);
    }

    private static string TruncateQuery(string query)
    {
        if (query.Length <= MaximumQueryLength)
        {
            return query;
        }

        var length = MaximumQueryLength;
        if (char.IsHighSurrogate(query[length - 1]))
        {
            length--;
        }

        return query[..length];
    }

    private static string CreateExcerpt(IndexedBlock block, IReadOnlyList<string> terms)
        => CreateExcerpt(block.Body, terms);

    private static string CreateExcerpt(string body, IReadOnlyList<string> terms)
    {
        if (body.Length <= MaximumExcerptLength)
        {
            return body;
        }

        var normalizedBody = NormalizeWithOffsets(body);
        var normalizedMatch = terms
            .Select(term => normalizedBody.Text.IndexOf(term, StringComparison.Ordinal))
            .Where(position => position >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var originalMatch = normalizedBody.OriginalOffsets.Length == 0
            ? 0
            : normalizedBody.OriginalOffsets[Math.Min(normalizedMatch, normalizedBody.OriginalOffsets.Length - 1)];
        var start = Math.Clamp(originalMatch - 100, 0, body.Length - MaximumExcerptLength);
        return body.Substring(start, MaximumExcerptLength).Trim();
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(term, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += term.Length;
        }

        return count;
    }

    private static string Normalize(string value)
    {
        var text = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var rune in value.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (!previousWasWhitespace)
                {
                    text.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            previousWasWhitespace = false;
            text.Append(Rune.ToLowerInvariant(rune));
        }

        return text.ToString();
    }

    private static NormalizedText NormalizeWithOffsets(string value) => NormalizeCore(value);

    private static NormalizedText NormalizeCore(string value)
    {
        var text = new StringBuilder(value.Length);
        var offsets = new List<int>(Math.Min(value.Length, MaximumBlockLength));
        var elements = StringInfo.GetTextElementEnumerator(value);
        var previousWasWhitespace = false;
        while (elements.MoveNext())
        {
            var originalOffset = elements.ElementIndex;
            var element = elements.GetTextElement().Normalize(NormalizationForm.FormD);
            foreach (var rune in element.EnumerateRunes())
            {
                if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (Rune.IsWhiteSpace(rune))
                {
                    if (!previousWasWhitespace)
                    {
                        text.Append(' ');
                        offsets.Add(originalOffset);
                        previousWasWhitespace = true;
                    }

                    continue;
                }

                previousWasWhitespace = false;
                var lowered = Rune.ToLowerInvariant(rune).ToString();
                text.Append(lowered);
                for (var index = 0; index < lowered.Length; index++)
                {
                    offsets.Add(originalOffset);
                }
            }
        }

        return new NormalizedText(text.ToString(), offsets.ToArray());
    }

    private sealed class SectionMatch
    {
        private readonly string[] _tails;
        private string? _excerpt;
        private string _rawTail = string.Empty;

        public SectionMatch(IndexedBlock block, IReadOnlyList<string> terms)
        {
            Title = block.Title;
            Section = block.Section;
            RelativePath = block.RelativePath;
            NormalizedTitle = block.NormalizedTitle;
            NormalizedSection = block.NormalizedSection;
            MatchedTerms = new bool[terms.Count];
            BodyOccurrences = new int[terms.Count];
            _tails = new string[terms.Count];
        }

        public string Title { get; }
        public string Section { get; }
        public string RelativePath { get; }
        public string NormalizedTitle { get; }
        public string NormalizedSection { get; }
        public bool[] MatchedTerms { get; }
        public int[] BodyOccurrences { get; }

        public void Add(IndexedBlock block, IReadOnlyList<string> terms, CancellationToken cancellationToken)
        {
            _excerpt ??= block.Body.Length <= MaximumExcerptLength
                ? block.Body
                : block.Body[..MaximumExcerptLength].Trim();
            var rawBoundary = _rawTail + block.Body;
            var normalizedRawTail = Normalize(_rawTail);
            var normalizedBoundary = Normalize(rawBoundary);
            for (var index = 0; index < terms.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var term = terms[index];
                var bodyWithBoundary = (_tails[index] ?? string.Empty) + block.NormalizedBody;
                var occurrences = CountOccurrences(bodyWithBoundary, term);
                BodyOccurrences[index] += occurrences;
                var headingMatch = NormalizedTitle.Contains(term, StringComparison.Ordinal)
                    || NormalizedSection.Contains(term, StringComparison.Ordinal);
                MatchedTerms[index] |= headingMatch || occurrences > 0;
                if (block.NormalizedBody.Contains(term, StringComparison.Ordinal))
                {
                    _excerpt = CreateExcerpt(block, terms);
                }
                else if (!normalizedRawTail.Contains(term, StringComparison.Ordinal)
                    && normalizedBoundary.Contains(term, StringComparison.Ordinal))
                {
                    _excerpt = CreateExcerpt(rawBoundary, terms);
                }

                var tailLength = Math.Min(Math.Max(0, term.Length - 1), bodyWithBoundary.Length);
                _tails[index] = bodyWithBoundary[^tailLength..];
            }

            var rawTailLength = Math.Min(MaximumQueryLength, rawBoundary.Length);
            var rawTailStart = rawBoundary.Length - rawTailLength;
            if (rawTailStart < rawBoundary.Length && char.IsLowSurrogate(rawBoundary[rawTailStart]))
            {
                rawTailStart++;
            }

            _rawTail = rawBoundary[rawTailStart..];
        }

        public PedagogicGuidanceSearchResult ToResult(IReadOnlyList<string> terms)
        {
            var score = MatchedTerms.All(found => found) ? 3 : 0;
            for (var index = 0; index < terms.Count; index++)
            {
                var sectionMatch = NormalizedSection.Contains(terms[index], StringComparison.Ordinal);
                var titleMatch = NormalizedTitle.Contains(terms[index], StringComparison.Ordinal);
                if (sectionMatch || (titleMatch && BodyOccurrences.Any(count => count > 0)))
                {
                    score += 4;
                }
                else if (titleMatch)
                {
                    // A document title is weak evidence for an unrelated section.
                    score += 1;
                }

                score += Math.Min(BodyOccurrences[index], 2);
            }

            var editorialSection = EditorialTerms.Any(term => NormalizedSection.Contains(term, StringComparison.Ordinal));
            var editorialQuery = terms.Any(queryTerm => EditorialTerms.Any(term => term.Contains(queryTerm, StringComparison.Ordinal)));
            if (editorialSection && !editorialQuery) score = Math.Max(1, score / 10);

            return new PedagogicGuidanceSearchResult(Title, Section, RelativePath, _excerpt ?? string.Empty, score);
        }
    }

    private sealed record IndexedBlock(
        string Title,
        string Section,
        string RelativePath,
        int SectionOrdinal,
        string Body,
        string NormalizedTitle,
        string NormalizedSection,
        string NormalizedBody);
    private sealed record NormalizedText(string Text, int[] OriginalOffsets);
    private readonly record struct SectionKey(string RelativePath, int SectionOrdinal);

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();
}
