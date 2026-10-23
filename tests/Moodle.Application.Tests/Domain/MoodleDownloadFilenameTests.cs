using Moodle.Domain;

namespace Moodle.Application.Tests.Domain;

public sealed class MoodleDownloadFilenameTests
{
    [Theory]
    [InlineData("../../a.pdf", "a.pdf")]
    [InlineData("..\\..\\a.pdf", "a.pdf")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("Aula sintética.pdf", "Aula sintética.pdf")]
    [InlineData("bad:name?.pdf", "badname.pdf")]
    public void SanitizesNamesOnBothWindowsAndLinux(string candidate, string expected) =>
        Assert.Equal(expected, MoodleDownloadFilename.Sanitize(candidate));

    [Fact]
    public void DoesNotUseAuthenticatedQueryAsFilename()
    {
        Assert.Equal("arquivo", MoodleDownloadFilename.Resolve(null, null, new Uri("https://example.invalid/pluginfile.php?token=synthetic")));
        Assert.Equal("Aula sintética.pdf", MoodleDownloadFilename.Resolve(null, null, new Uri("https://example.invalid/pluginfile.php/1/Aula%20sint%C3%A9tica.pdf?token=synthetic")));
        Assert.Equal("metadata.pdf", MoodleDownloadFilename.Resolve(null, "metadata.pdf", new Uri("https://example.invalid/pluginfile.php/1/source.pdf")));
    }

    [Fact]
    public void BoundsFilenameAndKeepsExtension()
    {
        var result = MoodleDownloadFilename.Sanitize(new string('a', 250) + ".pdf")!;
        Assert.True(result.Length <= 180);
        Assert.EndsWith(".pdf", result);
    }
}
