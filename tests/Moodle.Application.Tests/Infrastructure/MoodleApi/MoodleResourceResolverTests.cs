using Moodle.Application.MoodleApi;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleResourceResolverTests
{
    private readonly MoodleResourceResolver _sut = new();

    [Theory]
    [InlineData("33587", MoodleResourceType.CourseId, "33587")]
    [InlineData("categoryid=8270", MoodleResourceType.CategoryId, "8270")]
    [InlineData("https://moodle.example/course/view.php?id=33587", MoodleResourceType.CourseUrl, "33587")]
    [InlineData("https://moodle.example/course/index.php?categoryid=8270", MoodleResourceType.CategoryUrl, "8270")]
    [InlineData("idnumber:1072716", MoodleResourceType.IdNumber, "1072716")]
    [InlineData("shortname:ARQ-HW", MoodleResourceType.ShortName, "ARQ-HW")]
    public void Resolve_ClassificaEntradasMoodle(string input, MoodleResourceType expectedType, string expectedValue)
    {
        var result = _sut.Resolve(input);

        Assert.Equal(expectedType, result.Type);
        Assert.Equal(expectedValue, result.Value);
    }
}
