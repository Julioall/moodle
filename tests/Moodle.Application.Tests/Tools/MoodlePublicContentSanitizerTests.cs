using Moodle.Application.Tools;

namespace Moodle.Application.Tests.Tools;

public sealed class MoodlePublicContentSanitizerTests
{
    [Theory]
    [InlineData("<p>Aula útil</p><input name='sesskey' value='synthetic-canary'>")]
    [InlineData("<input value='synthetic-canary' type='hidden' name='sesskey'><p>Aula útil</p>")]
    [InlineData("const config = { launch_token: 'synthetic-canary' }; // Aula útil")]
    [InlineData("Aula útil https://example.invalid/x?token=synthetic-canary&chapter=1")]
    [InlineData("Aula útil token%3Dsynthetic-canary%26chapter%3D1")]
    [InlineData("Aula útil token&#61;synthetic-canary&amp;chapter=1")]
    [InlineData("Aula útil Authorization: Bearer synthetic-canary")]
    [InlineData("Aula útil Authorization: Basic user:synthetic-canary")]
    [InlineData("Aula útil Cookie: MoodleSession=synthetic-canary; Path=/")]
    [InlineData("Aula útil Set-Cookie: MoodleSession=synthetic-canary; Path=/")]
    [InlineData("Aula útil sessionKey=synthetic-canary")]
    [InlineData("Aula útil token%25253Dsynthetic-canary%252526chapter%253D1")]
    [InlineData("Aula útil <input name='sesskey' value='prefix>synthetic-canary'>")]
    [InlineData("Aula útil const launch_token='prefix\\'synthetic-canary';")]
    public void RedactsRecognizedEmbeddedSecretsAndPreservesLesson(string text)
    {
        var result = MoodlePublicContentSanitizer.ToSanitizedElement(new { html = text });
        var sanitized = result.GetProperty("html").GetString()!;
        Assert.DoesNotContain("synthetic-canary", sanitized);
        Assert.Contains("Aula útil", sanitized);
    }

    [Fact]
    public void SanitizesArraysAndStructuredPropertiesWithoutChangingOriginal()
    {
        var original = new { token = "synthetic-canary", files = new[] { new { text = "sesskey='synthetic-canary'" } } };
        var result = MoodlePublicContentSanitizer.ToSanitizedElement(original);
        Assert.DoesNotContain("synthetic-canary", result.GetRawText());
        Assert.Equal("synthetic-canary", original.token);
    }

    [Fact]
    public void WithholdsOversizedTextRatherThanReturningUninspectedSuffix()
    {
        Assert.Equal(MoodlePublicContentSanitizer.Withheld,
            MoodlePublicContentSanitizer.SanitizeText(new string('x', 1_000_001) + "sesskey=synthetic-canary"));
    }

    [Fact]
    public void InspectsRootStringsAndDeclaresPartialWhenTextIsWithheld()
    {
        var root = MoodlePublicContentSanitizer.Inspect("sesskey=synthetic-canary");
        Assert.DoesNotContain("synthetic-canary", root.Payload.GetString());
        Assert.True(root.Complete);
        var oversized = MoodlePublicContentSanitizer.Inspect(new { html = new string('x', 1_000_001) });
        Assert.False(oversized.Complete);
        Assert.Equal(MoodlePublicContentSanitizer.Withheld, oversized.Payload.GetProperty("html").GetString());
    }

    [Fact]
    public void RejectsProjectionSizeAndDepthBeforePublishingOriginal()
    {
        Assert.Throws<InvalidOperationException>(() => MoodlePublicContentSanitizer.Inspect(new string('x', 8_000_001)));
        var root = new System.Text.Json.Nodes.JsonObject();
        var current = root;
        for (var i = 0; i < 34; i++) { var child = new System.Text.Json.Nodes.JsonObject(); current["child"] = child; current = child; }
        current["html"] = "sesskey=synthetic-canary";
        Assert.Throws<InvalidOperationException>(() => MoodlePublicContentSanitizer.Inspect(root));
    }

    [Fact]
    public void LeavesOrdinaryEncodedLessonUntouched()
    {
        const string text = "<p>Use A &amp; B; chapter%20one</p>";
        Assert.Equal(text, MoodlePublicContentSanitizer.SanitizeText(text));
    }

    [Fact]
    public void WithholdsContentThatExceedsTheSupportedDecodeDepth()
    {
        const string text = "Aula útil token%2525253Dsynthetic-canary";
        Assert.Equal(MoodlePublicContentSanitizer.Withheld, MoodlePublicContentSanitizer.SanitizeText(text));
    }
}
