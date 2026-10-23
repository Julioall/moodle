namespace Moodle.Application.Pedagogy;

public interface IPedagogicGuidanceSearch
{
    Task<IReadOnlyList<PedagogicGuidanceSearchResult>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record PedagogicGuidanceSearchResult(
    string Title,
    string Section,
    string RelativePath,
    string Excerpt,
    int Score);
