namespace Moodle.Benchmarks.Cognitive;

public enum FailureTaxonomy
{
    None = 0,
    IntentMisclassified,
    SkillNotSelected,
    WrongOperation,
    InvalidParameters,
    WrongConnection,
    CapabilityMiss,
    DiscoveryMiss,
    PolicyBlockUnexpected,
    PaginationIncomplete,
    NormalizationDataLoss,
    ResultInterpretation,
    Hallucination,
    MoodleError,
    Timeout
}
