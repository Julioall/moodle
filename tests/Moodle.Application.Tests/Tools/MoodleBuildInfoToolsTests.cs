using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Moodle.Presentation.Tools;

namespace Moodle.Application.Tests.Tools;

public sealed class MoodleBuildInfoToolsTests
{
    [Fact]
    public void Resolve_build_identity_prefers_explicit_deployment_metadata()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Moodle:Build:Version"] = "2026.09.08",
                ["Moodle:Build:Commit"] = "abc123456789",
                ["Moodle:Build:Id"] = "run-33447-7",
                ["Moodle:Build:BuiltAt"] = "2026-09-08T12:00:00Z",
                ["Moodle:Build:DeployedAt"] = "2026-09-08T12:10:00Z",
                ["Moodle:Build:SourceRef"] = "main",
                ["Moodle:Build:Environment"] = "Production"
            })
            .Build();
        var provider = new ConnectorBuildInfoProvider(configuration);

        var info = provider.Get(_ => null);

        Assert.Equal("2026.09.08", info.Version);
        Assert.Equal("abc123456789", info.Commit);
        Assert.Equal("run-33447-7", info.BuildId);
        Assert.Equal(DateTimeOffset.Parse("2026-09-08T12:00:00Z"), info.BuiltAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-08T12:10:00Z"), info.DeployedAt);
        Assert.Equal("main", info.SourceRef);
        Assert.Equal("Production", info.Environment);
        Assert.Equal("complete", info.IdentityStatus);
        Assert.True(info.CanCompareToRepository);
        Assert.Equal("configuration:Moodle:Build:Commit", info.CommitSource);
    }

    [Fact]
    public void Resolve_build_identity_supports_common_ci_environment_names()
    {
        var configuration = new ConfigurationBuilder().Build();
        var provider = new ConnectorBuildInfoProvider(configuration);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GITHUB_SHA"] = "fedcba987654",
            ["GITHUB_RUN_ID"] = "9911",
            ["BUILD_DATE"] = "2026-09-08T13:00:00Z",
            ["GITHUB_REF_NAME"] = "main",
            ["ASPNETCORE_ENVIRONMENT"] = "Production"
        };

        var info = provider.Get(key => environment.TryGetValue(key, out var value) ? value : null);

        Assert.Equal("fedcba987654", info.Commit);
        Assert.Equal("9911", info.BuildId);
        Assert.Equal(DateTimeOffset.Parse("2026-09-08T13:00:00Z"), info.BuiltAt);
        Assert.Equal("main", info.SourceRef);
        Assert.True(info.CanCompareToRepository);
        Assert.Equal("complete", info.IdentityStatus);
    }

    [Fact]
    public void Tool_returns_structured_runtime_identity_with_audit_id()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MOODLE_VERSION"] = "2026.09.08",
                ["MOODLE_COMMIT"] = "abc123456789",
                ["MOODLE_BUILD_ID"] = "run-7"
            })
            .Build();
        var provider = new ConnectorBuildInfoProvider(configuration);
        var result = new MoodleBuildInfoTools(provider).GetConnectorBuildInfo();

        Assert.False(result.IsError);
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        Assert.Equal("ok", structured.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("auditId").GetString()));
        Assert.Equal("moodle", structured.GetProperty("data").GetProperty("component").GetString());
        Assert.Equal("abc123456789", structured.GetProperty("data").GetProperty("commit").GetString());
        Assert.True(structured.GetProperty("data").GetProperty("canCompareToRepository").GetBoolean());
        Assert.DoesNotContain("password", structured.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_configuration_wins_over_ambient_ci_sha()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MOODLE_COMMIT"] = "explicit-runtime-commit"
            })
            .Build();
        var provider = new ConnectorBuildInfoProvider(configuration);

        var info = provider.Get(key => key == "GITHUB_SHA" ? "ambient-ci-commit" : null);

        Assert.Equal("explicit-runtime-commit", info.Commit);
        Assert.Equal("configuration:MOODLE_COMMIT", info.CommitSource);
    }
}
