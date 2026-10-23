using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moodle.Domain.Benchmarking;

namespace Moodle.Application.Benchmarking;

public interface IShadowComparisonRunner
{
    Task<ShadowComparisonResult> RunComparisonAsync(
        string operationName,
        Moodle.Domain.Registry.ConnectionInfo connection,
        string moodleVersion,
        string profileName,
        Func<Task<JsonNode?>> legacyExecution,
        Func<Task<(JsonNode? Result, string PolicyDecision)>> registryExecution);
}

public sealed class ShadowComparisonRunner : IShadowComparisonRunner
{
    private readonly IEnumerable<IShadowComparisonProfile> _profiles;

    public ShadowComparisonRunner(IEnumerable<IShadowComparisonProfile> profiles)
    {
        _profiles = profiles;
    }

    public async Task<ShadowComparisonResult> RunComparisonAsync(
        string operationName,
        Moodle.Domain.Registry.ConnectionInfo connection,
        string moodleVersion,
        string profileName,
        Func<Task<JsonNode?>> legacyExecution,
        Func<Task<(JsonNode? Result, string PolicyDecision)>> registryExecution)
    {
        var profile = _profiles.FirstOrDefault(p => p.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Comparison profile '{profileName}' not found.");

        // 1. Run Legacy
        var legacySw = Stopwatch.StartNew();
        var legacyResult = await legacyExecution();
        legacySw.Stop();

        var legacyBytes = GetPayloadBytes(legacyResult);
        var legacyTrace = new LegacyTrace(legacySw.ElapsedMilliseconds, legacyBytes, MoodleCalls: 1, legacyResult);

        // 2. Run Registry (SafeReadExecutor)
        var registrySw = Stopwatch.StartNew();
        var (registryResult, policyDecision) = await registryExecution();
        registrySw.Stop();

        var registryBytes = GetPayloadBytes(registryResult);
        var registryTrace = new RegistryTrace(
            DurationMs: registrySw.ElapsedMilliseconds,
            RawPayloadBytes: registryBytes, // Note: For true raw bytes we'd need to intercept the HTTP call, but this is an approximation
            NormalizedPayloadBytes: registryBytes,
            PolicyDecision: policyDecision,
            MoodleCalls: 1,
            Result: registryResult
        );

        // 3. Compare
        var latencyDeltaMs = registryTrace.DurationMs - legacyTrace.DurationMs;

        var payloadReductionPercent = 0.0;
        if (legacyBytes > 0)
        {
            payloadReductionPercent = Math.Max(0, 100.0 - ((double)registryTrace.NormalizedPayloadBytes / legacyBytes * 100.0));
        }

        var comparisonMetrics = profile.Compare(legacyResult, registryResult, latencyDeltaMs, payloadReductionPercent);

        if (comparisonMetrics.SemanticParityPercent == 100.0)
        {
            var evidence = new Moodle.Domain.Registry.ValidationEvidence(
                OperationName: operationName,
                ConnectionId: connection.ConnectionId,
                AliasAtValidation: connection.Alias,
                NormalizationProfile: profileName,
                MoodleVersion: moodleVersion,
                SemanticParityPercent: comparisonMetrics.SemanticParityPercent,
                ValidatedAt: DateTimeOffset.UtcNow
            );

            var evidenceDir = ResolveEvidenceDirectory();
            Directory.CreateDirectory(evidenceDir);
            var evidenceFile = Path.Combine(evidenceDir, $"{operationName}_{connection.Alias}.json");
            await File.WriteAllTextAsync(evidenceFile, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }

        return new ShadowComparisonResult(legacyTrace, registryTrace, comparisonMetrics);
    }

    private static long GetPayloadBytes(JsonNode? node)
    {
        if (node == null) return 0;
        // Approximation: serialize to string and get UTF8 bytes
        return System.Text.Encoding.UTF8.GetByteCount(node.ToJsonString());
    }

    private static string ResolveEvidenceDirectory()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Moodle.sln")))
            {
                return Path.Combine(directory.FullName, ".moodlebench", "evidence");
            }

            directory = directory.Parent;
        }

        return Path.Combine(Environment.CurrentDirectory, ".moodlebench", "evidence");
    }
}
