using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Tools;
using Moodle.Presentation.Configuration;
using Microsoft.Extensions.Configuration;

namespace Moodle.Presentation.Tools;

/// <summary>
/// Resolves the identity of the Connector process from deployment metadata.
/// Values are intentionally read-only and never inferred from secrets or
/// Moodle state.
/// </summary>
public sealed class ConnectorBuildInfoProvider(IConfiguration configuration)
{
    public ConnectorBuildInfo Get() => Get(Environment.GetEnvironmentVariable);

    internal ConnectorBuildInfo Get(Func<string, string?> environmentLookup)
    {
        var assembly = typeof(ConnectorBuildInfoProvider).Assembly;
        var version = ReadSetting(
            environmentLookup,
            ["MOODLE_VERSION", "VERSION"],
            ["Moodle:Build:Version"]);
        if (version.Value is null)
        {
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            version = (Normalize(informationalVersion), "assembly:AssemblyInformationalVersion");
        }
        if (version.Value is null)
        {
            version = (Normalize(assembly.GetName().Version?.ToString()), "assembly:Version");
        }

        var commit = ReadSetting(
            environmentLookup,
            [
                "MOODLE_COMMIT", "GIT_COMMIT", "GIT_COMMIT_SHA", "CI_COMMIT_SHA",
                "GITHUB_SHA", "SOURCE_VERSION"
            ],
            ["Moodle:Build:Commit"]);
        var buildId = ReadSetting(
            environmentLookup,
            ["MOODLE_BUILD_ID", "BUILD_ID", "BUILD_BUILDID", "CI_PIPELINE_ID", "GITHUB_RUN_ID"],
            ["Moodle:Build:Id"]);
        var builtAt = ReadTimestamp(
            environmentLookup,
            ["MOODLE_BUILD_AT", "BUILD_DATE", "BUILD_TIMESTAMP", "CI_PIPELINE_CREATED_AT"],
            ["Moodle:Build:BuiltAt"]);
        var deployedAt = ReadTimestamp(
            environmentLookup,
            ["MOODLE_DEPLOYED_AT", "DEPLOYED_AT", "DEPLOY_DATE"],
            ["Moodle:Build:DeployedAt"]);
        var sourceRef = ReadSetting(
            environmentLookup,
            ["MOODLE_SOURCE_REF", "GITHUB_REF_NAME", "CI_COMMIT_REF_NAME", "GITHUB_REF"],
            ["Moodle:Build:SourceRef"]);
        var environment = ReadSetting(
            environmentLookup,
            ["ASPNETCORE_ENVIRONMENT"],
            ["Moodle:Build:Environment"]);

        var identityStatus = commit.Value is not null
            ? buildId.Value is not null ? "complete" : "identified"
            : version.Value is not null || buildId.Value is not null || builtAt.Value is not null
                ? "partial"
                : "unknown";
        var canCompareToRepository = commit.Value is not null;
        var verification = canCompareToRepository
            ? "Compare o campo commit com o SHA esperado do repositorio. Esta leitura identifica o runtime, mas nao afirma que ele e o ultimo commit da branch."
            : "Configure MOODLE_COMMIT (ou GIT_COMMIT) para permitir a comparacao exata com o repositorio.";

        return new ConnectorBuildInfo(
            Component: "moodle",
            Version: version.Value,
            VersionSource: version.Source,
            Commit: commit.Value,
            CommitSource: commit.Source,
            BuildId: buildId.Value,
            BuildIdSource: buildId.Source,
            BuiltAt: builtAt.Value,
            BuiltAtSource: builtAt.Source,
            DeployedAt: deployedAt.Value,
            DeployedAtSource: deployedAt.Source,
            SourceRef: sourceRef.Value,
            Environment: environment.Value,
            IdentityStatus: identityStatus,
            CanCompareToRepository: canCompareToRepository,
            Verification: verification);
    }

    private (string? Value, string? Source) ReadSetting(
        Func<string, string?> environmentLookup,
        IReadOnlyList<string> environmentKeys,
        IReadOnlyList<string> configurationKeys)
    {
        foreach (var key in configurationKeys.Concat(environmentKeys))
        {
            var value = Normalize(configuration[key]);
            if (value is not null)
            {
                return (value, $"configuration:{key}");
            }
        }

        foreach (var key in environmentKeys)
        {
            var value = Normalize(environmentLookup(key));
            if (value is not null)
            {
                return (value, $"environment:{key}");
            }
        }

        return (null, null);
    }

    private (DateTimeOffset? Value, string? Source) ReadTimestamp(
        Func<string, string?> environmentLookup,
        IReadOnlyList<string> environmentKeys,
        IReadOnlyList<string> configurationKeys)
    {
        var raw = ReadSetting(environmentLookup, environmentKeys, configurationKeys);
        if (raw.Value is null || !DateTimeOffset.TryParse(
                raw.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return (null, raw.Source);
        }

        return (parsed, raw.Source);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            normalized.Any(char.IsControl) ||
            normalized.Length > 256)
        {
            return null;
        }

        return normalized;
    }
}

[McpServerToolType]
public sealed class MoodleBuildInfoTools(ConnectorBuildInfoProvider buildInfoProvider)
{
    public const string ToolName = "get_connector_build_info";

    [McpServerTool(
        Name = ToolName,
        Title = "Get Connector Build Info",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ConnectorBuildInfo>))]
    [MoodleToolMetadata(
        Family = "infrastructure",
        Classification = "R6",
        Kind = "diagnostic",
        CanonicalOperation = "connector.diagnostics.build_identity",
        Structural = true,
        ExposureStatus = "Keep",
        ExposureReason = "Identidade somente leitura do runtime para comparar a instancia implantada com o repositorio.",
        Evidence = "MoodleBuildInfoTools.GetConnectorBuildInfo; valores injetados pelo ambiente de deploy ou derivados dos metadados do assembly.",
        RequiredPlatformPermission = "tool.connections.manage")]
    [Description("Retorna a identidade de build do Moodle em execucao: versao, commit, build ID, data de build, data de deploy e referencia de origem. Nao consulta nem altera o Moodle. Compare o commit retornado com o SHA esperado do repositorio.")]
    public CallToolResult GetConnectorBuildInfo()
    {
        var data = buildInfoProvider.Get();
        var warnings = data.CanCompareToRepository
            ? Array.Empty<string>()
            : new[] { "A instancia nao expos commit de runtime; configure MOODLE_COMMIT ou GIT_COMMIT para habilitar a comparacao com o repositorio." };
        var response = new ToolResponse<ConnectorBuildInfo>(
            "ok",
            data,
            warnings,
            AuditId: null,
            DateTimeOffset.UtcNow,
            Message: data.Verification);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = data.Verification }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }
}

public sealed record ConnectorBuildInfo(
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("versionSource")] string? VersionSource,
    [property: JsonPropertyName("commit")] string? Commit,
    [property: JsonPropertyName("commitSource")] string? CommitSource,
    [property: JsonPropertyName("buildId")] string? BuildId,
    [property: JsonPropertyName("buildIdSource")] string? BuildIdSource,
    [property: JsonPropertyName("builtAt")] DateTimeOffset? BuiltAt,
    [property: JsonPropertyName("builtAtSource")] string? BuiltAtSource,
    [property: JsonPropertyName("deployedAt")] DateTimeOffset? DeployedAt,
    [property: JsonPropertyName("deployedAtSource")] string? DeployedAtSource,
    [property: JsonPropertyName("sourceRef")] string? SourceRef,
    [property: JsonPropertyName("environment")] string? Environment,
    [property: JsonPropertyName("identityStatus")] string IdentityStatus,
    [property: JsonPropertyName("canCompareToRepository")] bool CanCompareToRepository,
    [property: JsonPropertyName("verification")] string Verification);
