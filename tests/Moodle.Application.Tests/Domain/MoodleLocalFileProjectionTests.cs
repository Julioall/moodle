using Moodle.Domain;

namespace Moodle.Application.Tests.Domain;

public sealed class MoodleLocalFileProjectionTests
{
    [Fact]
    public void KeepsOnlyLocalFilesAndDoesNotMutateMixedModule()
    {
        var local = new CourseModuleFile("file", "lesson.pdf", "/", 3, "application/pdf", "https://example.invalid/pluginfile.php/1/lesson.pdf", false);
        var url = local with { Type = "url", FileUrl = "https://external.invalid" };
        var external = local with { IsExternalFile = true };
        var unknown = local with { Type = null };
        var module = new CourseModuleSummary("11", "22", "resource", "Synthetic", null, true, true, null, null, [], [local, url, external, unknown]);
        var projected = MoodleLocalFileProjection.Project(module);
        Assert.Equal(local, Assert.Single(projected.Files));
        Assert.Equal(4, module.Files.Count);
        Assert.Equal(module.ModuleId, projected.ModuleId);
    }
}
