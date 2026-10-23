using System.Collections.Generic;

namespace Moodle.Benchmarks.Cognitive;

public sealed record BenchmarkSkillBundle(
    IReadOnlyList<string> Names,
    string PromptSection,
    string ManifestHash)
{
    public bool IsEmpty => Names.Count == 0;

    public string SelectedNames => Names.Count == 0 ? "none" : string.Join(',', Names);
}
