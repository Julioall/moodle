namespace Moodle.Benchmarks.Cognitive;

using Moodle.Presentation.Configuration;

public sealed record BenchmarkProfile(
    ToolExposureProfile Exposure,
    string ModelName,
    bool UsePluginSkills
);
