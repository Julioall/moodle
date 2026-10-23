using Moodle.Benchmarks.Cognitive;
using Moodle.Presentation.Configuration;

namespace Moodle.Application.Tests.Benchmarking;

public sealed class BenchmarkPluginSkillBundleTests
{
    [Fact]
    public void Plugin_profile_loads_the_exact_packaged_skill_content()
    {
        var profile = new BenchmarkProfile(ToolExposureProfile.FullWithCoursesSkill, "test-model", UsePluginSkills: true);
        var task = new BenchmarkTask(
            Id: "courses.list",
            Category: "courses",
            Prompt: "Liste meus cursos.",
            ExpectedIntent: "courses.list",
            AllowedOperations: ["list_my_courses"],
            ForbiddenOperations: [],
            RequiresCompleteDataset: false);

        var bundle = OpenAIResponsesBenchmarkDriver.LoadPluginSkillBundle(profile, task, FindRepositoryRoot());

        Assert.Equal(["moodle-core", "moodle-courses"], bundle.Names);
        Assert.Contains("# moodle-core", bundle.PromptSection, StringComparison.Ordinal);
        Assert.Contains("# moodle-courses", bundle.PromptSection, StringComparison.Ordinal);
        Assert.Matches("^[a-f0-9]{64}$", bundle.ManifestHash);
    }

    [Fact]
    public void Mcp_only_profile_does_not_claim_to_load_a_skill()
    {
        var profile = new BenchmarkProfile(ToolExposureProfile.Full, "test-model", UsePluginSkills: false);
        var task = new BenchmarkTask(
            Id: "courses.list",
            Category: "courses",
            Prompt: "Liste meus cursos.",
            ExpectedIntent: "courses.list",
            AllowedOperations: ["list_my_courses"],
            ForbiddenOperations: [],
            RequiresCompleteDataset: false);

        var bundle = OpenAIResponsesBenchmarkDriver.LoadPluginSkillBundle(profile, task, FindRepositoryRoot());

        Assert.Empty(bundle.Names);
        Assert.Empty(bundle.PromptSection);
        Assert.Empty(bundle.ManifestHash);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Moodle.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root for benchmark tests.");
    }
}
