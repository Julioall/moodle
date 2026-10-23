using Moodle.Infrastructure.MoodleApi;
using Xunit;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleInstallationIdentityTests
{
    [Fact]
    public void Normalizes_host_and_trailing_path_slash_but_keeps_distinct_installation_paths()
    {
        var first = MoodleInstallationIdentity.Create(new Uri("https://Moodle.Example.org/ead"));
        var normalized = MoodleInstallationIdentity.Create(new Uri("https://moodle.example.org/ead/"));
        var differentPath = MoodleInstallationIdentity.Create(new Uri("https://moodle.example.org/other"));

        Assert.Equal(first, normalized);
        Assert.NotEqual(first, differentPath);
        Assert.Equal(32, first.Length);
        Assert.DoesNotContain("moodle", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Excludes_credentials_query_and_fragment_from_budget_identity()
    {
        var plain = MoodleInstallationIdentity.Create(new Uri("https://moodle.example.org/ead"));
        var withNonAuthorityParts = MoodleInstallationIdentity.Create(new Uri("https://user:password@moodle.example.org/ead?token=synthetic#section"));

        Assert.Equal(plain, withNonAuthorityParts);
    }
}
