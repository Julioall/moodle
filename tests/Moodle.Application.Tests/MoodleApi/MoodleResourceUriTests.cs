using Moodle.Application.MoodleApi;

namespace Moodle.Application.Tests.MoodleApi;

public sealed class MoodleResourceUriTests
{
    [Fact]
    public void CreateAndParse_UseOnlyOpaqueId()
    {
        const string id = "0123456789abcdef0123456789abcdef";
        var uri = MoodleResourceUri.Create(id);

        Assert.Equal("moodle://resource/0123456789abcdef0123456789abcdef", uri);
        Assert.True(MoodleResourceUri.TryParse(uri, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData("moodle://resource/0123456789abcdef0123456789abcdef?token=secret")]
    [InlineData("moodle://resource/0123456789abcdef0123456789abcdef#fragment")]
    [InlineData("moodle://resource/../../course/33446")]
    [InlineData("moodle://resource/440752")]
    [InlineData("https://moodle.example/pluginfile.php/1/file.pdf")]
    public void TryParse_RejectsNonOpaqueOrSensitiveUris(string uri)
    {
        Assert.False(MoodleResourceUri.TryParse(uri, out _));
    }
}
