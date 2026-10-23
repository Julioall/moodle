using Moodle.Infrastructure.Pedagogy;
using System.Reflection;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MarkdownPedagogicGuidanceSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pedagogy-{Guid.NewGuid():N}");

    public MarkdownPedagogicGuidanceSearchTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SearchAsync_RanksTitleAndSectionMatchesAboveBodyMatches()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "body.md"), "# Outro guia\n## Prática\nA avaliação formativa ajuda a aprendizagem.");
        await File.WriteAllTextAsync(Path.Combine(_root, "title.md"), "# Avaliação Formativa\n## Estratégias de avaliação formativa\nUse devolutivas frequentes.");

        var results = await Search().SearchAsync("avaliacao formativa", 10, CancellationToken.None);

        Assert.Equal("title.md", results[0].RelativePath);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task SearchAsync_PrioritizesGuidanceOverEditorialFrequencyButKeepsAuthorshipDiscoverable()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "guide.md"),
            "# Avaliação\n## Créditos e autoria\nEquipe sintética. " + string.Concat(Enumerable.Repeat("feedback avaliacao ", 500)) +
            "\n## Devolutivas\nUse feedback com evidências de avaliação para apoiar a aprendizagem.\n## Logística\nReserva de salas.");
        var search = Search();
        var results = await search.SearchAsync("feedback", 10, CancellationToken.None);
        Assert.Equal("Devolutivas", results[0].Section);
        Assert.Contains(results, result => result.Section == "Créditos e autoria");

        var authorship = await search.SearchAsync("autoria", 10, CancellationToken.None);
        Assert.Equal("Créditos e autoria", authorship[0].Section);
        Assert.Contains("Equipe sintética", authorship[0].Excerpt);

        var assessment = await search.SearchAsync("avaliacao", 10, CancellationToken.None);
        Assert.True(assessment.First(result => result.Section == "Logística").Score <
                    assessment.First(result => result.Section == "Devolutivas").Score);
    }

    [Fact]
    public async Task SearchAsync_IncludesPartialMatchesAndAwardsExactScoreAndAllTermsBonus()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "all.md"), "# Avaliação\n## Formativa\navaliacao avaliacao formativa");
        await File.WriteAllTextAsync(Path.Combine(_root, "partial.md"), "# Avaliação\n## Diagnóstica\navaliacao");

        var results = await Search().SearchAsync("avaliacao formativa", 10, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(14, results[0].Score);
        Assert.Equal("all.md", results[0].RelativePath);
        Assert.Equal(5, results[1].Score);
        Assert.Equal("partial.md", results[1].RelativePath);
    }

    [Fact]
    public async Task SearchAsync_PreservesDocumentAndSectionMetadataAndReturnsRelevantExcerpt()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "guia.md"), "# Guia do Tutor\nIntrodução geral.\n## Avaliação contínua\nObserve evidências formativas e ofereça feedback.");

        var result = Assert.Single(await Search().SearchAsync("evidencias formativas", 10, CancellationToken.None));

        Assert.Equal("Guia do Tutor", result.Title);
        Assert.Equal("Avaliação contínua", result.Section);
        Assert.Equal("guia.md", result.RelativePath);
        Assert.Contains("evidências formativas", result.Excerpt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_IndexesOnlyMarkdownFilesAtRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "subdir"));
        var outsidePath = Path.Combine(Path.GetDirectoryName(_root)!, $"outside-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(Path.Combine(_root, "valid.md"), "# Válido\n## Tema\ntermo procurado");
        await File.WriteAllTextAsync(Path.Combine(_root, "ignored.txt"), "# Ignorado\ntermo procurado");
        await File.WriteAllTextAsync(Path.Combine(_root, "subdir", "nested.md"), "# Aninhado\ntermo procurado");
        await File.WriteAllTextAsync(outsidePath, "# Fora\ntermo procurado");

        try
        {
            var result = Assert.Single(await Search().SearchAsync("termo procurado", 10, CancellationToken.None));

            Assert.Equal("valid.md", result.RelativePath);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyWhenRootDoesNotExist()
    {
        var missing = new MarkdownPedagogicGuidanceSearch(Path.Combine(_root, "missing"));

        Assert.Empty(await missing.SearchAsync("qualquer", 10, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_IsDeterministicAndClampsLimitToTen()
    {
        for (var index = 0; index < 12; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"{index:D2}.md"), $"# Mesmo\n## Seção\ntermo {index}");
        }

        var search = Search();
        var first = await search.SearchAsync("termo", 99, CancellationToken.None);
        var repeated = await search.SearchAsync("termo", 99, CancellationToken.None);
        var minimum = await search.SearchAsync("termo", 0, CancellationToken.None);

        Assert.Equal(10, first.Count);
        Assert.Equal(first, repeated);
        Assert.Single(minimum);
        Assert.Equal("00.md", minimum[0].RelativePath);
    }

    [Fact]
    public async Task SearchAsync_TruncatesQueryToThreeHundredCharacters()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "guide.md"), $"# Guia\n{new string('x', 300)} termo-fora-do-limite");

        var result = await Search().SearchAsync(new string('x', 300) + " termo-fora-do-limite", 10, CancellationToken.None);

        Assert.Single(result);
    }

    [Fact]
    public async Task SearchAsync_SplitsLongSectionsWithoutLosingSearchableText()
    {
        var filler = new string('a', 1700);
        await File.WriteAllTextAsync(Path.Combine(_root, "long.md"), $"# Guia\n## Longa\n{filler} palavra-final");

        var result = Assert.Single(await Search().SearchAsync("palavra-final", 10, CancellationToken.None));

        Assert.Equal("Longa", result.Section);
        Assert.Contains("palavra-final", result.Excerpt);
    }

    [Fact]
    public async Task SearchAsync_UsesImmutableIndexBuiltAtConstruction()
    {
        var path = Path.Combine(_root, "guide.md");
        await File.WriteAllTextAsync(path, "# Guia\n## Seção\nconteúdo original");
        var search = Search();
        await File.WriteAllTextAsync(path, "# Alterado\nconteúdo substituído");

        var result = Assert.Single(await search.SearchAsync("original", 10, CancellationToken.None));

        Assert.Equal("Guia", result.Title);
    }

    [Fact]
    public async Task SearchAsync_IsSafeForConcurrentCalls()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "guide.md"), "# Guia\n## Seção\nconteúdo concorrente");
        var search = Search();

        var searches = Enumerable.Range(0, 20)
            .Select(_ => search.SearchAsync("concorrente", 10, CancellationToken.None));
        var results = await Task.WhenAll(searches);

        Assert.All(results, result => Assert.Equal(results[0], result));
    }

    [Fact]
    public async Task SearchAsync_ScoresWholeSectionWhenTermsAreSeparatedByChunkBoundary()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "boundary.md"),
            $"# Guia\n## Seção longa\nprimeiro {new string('x', 1700)} segundo");

        var result = Assert.Single(await Search().SearchAsync("primeiro segundo", 10, CancellationToken.None));

        Assert.Equal(5, result.Score);
    }

    [Fact]
    public async Task SearchAsync_FindsSingleTermSplitAcrossBlockBoundary()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "split.md"),
            $"# Guia\n## Seção\n{new string('x', 1596)}boundary");

        var result = Assert.Single(await Search().SearchAsync("boundary", 10, CancellationToken.None));

        Assert.Equal(4, result.Score);
        Assert.Contains("boundary", result.Excerpt);
    }

    [Fact]
    public async Task SearchAsync_MapsNormalizedWhitespaceOffsetBackToRelevantExcerpt()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "whitespace.md"),
            $"# Guia\n## Seção\ninício{new string(' ', 1000)}evidência relevante ao docente");

        var result = Assert.Single(await Search().SearchAsync("evidencia relevante", 10, CancellationToken.None));

        Assert.Contains("evidência relevante", result.Excerpt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_ObservesPreCancelledToken()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "guide.md"), "# Guia\nconteúdo");
        var search = Search();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => search.SearchAsync("conteúdo", 10, cancellation.Token));
    }

    [Fact]
    public void Constructor_ObservesCancellationWhileBuildingIndex()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => new MarkdownPedagogicGuidanceSearch(_root, cancellation.Token));
    }

    [Fact]
    public async Task SearchAsync_NormalizesSupplementaryRunesWithoutBreakingEmoji()
    {
        const string deseretUppercase = "𐐀";
        const string deseretLowercase = "𐐨";
        await File.WriteAllTextAsync(Path.Combine(_root, "emoji.md"), $"# 🚀 {deseretUppercase}\n## 😀\nConteúdo 🧑‍🏫 especial");

        var results = await Search().SearchAsync($"🚀 {deseretLowercase} 🧑‍🏫", 10, CancellationToken.None);

        Assert.Single(results);
    }

    [Fact]
    public async Task Constructor_RetainsOnlyBoundedBodyBlocks()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "large.md"),
            $"# Guia\n## Extensa\n{new string('a', 10_000)}");

        var search = Search();
        var indexField = typeof(MarkdownPedagogicGuidanceSearch)
            .GetField("_index", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var blocks = Assert.IsAssignableFrom<System.Collections.IEnumerable>(indexField.GetValue(search));

        foreach (var block in blocks)
        {
            var body = Assert.IsType<string>(block!.GetType().GetProperty("Body")!.GetValue(block));
            Assert.InRange(body.Length, 0, 1600);
        }
    }

    private MarkdownPedagogicGuidanceSearch Search() => new(_root);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
