using Moodle.Application.Abstractions;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleConnectionAliasTests
{
    [Theory]
    [InlineData("goias", "goias")]
    [InlineData(" Goiás ", "goias")]
    [InlineData("GOIÁS", "goias")]
    [InlineData("SENAI", "senai")]
    public void Normalize_CanonicalizaAliasSemPerderIdentidade(string input, string expected)
    {
        Assert.Equal(expected, MoodleConnectionAlias.Normalize(input));
    }

    [Fact]
    public void NormalizeOrDefault_UsaDefaultSomenteParaValorAusente()
    {
        Assert.Equal("default", MoodleConnectionAlias.NormalizeOrDefault("  "));
    }
}
