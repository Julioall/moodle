namespace Moodle.Domain.Grading;

/// <summary>
/// Vocabulário estável para localizar uma falha operacional sem inferir sua
/// causa. A causa técnica continua em LastErrorCode; fases desconhecidas de
/// dados legados permanecem explícitas como unknown.
/// </summary>
public static class GradingFailurePhase
{
    public const string Unknown = "unknown";
    public const string Query = "query";
    public const string Acquisition = "acquisition";
    public const string Context = "context";
    public const string Package = "package";
    public const string Analysis = "analysis";
    public const string Save = "save";
    public const string Preview = "preview";
    public const string Publication = "publication";

    public static bool IsKnown(string? value) => value switch
    {
        Unknown or Query or Acquisition or Context or Package or Analysis or Save or Preview or Publication => true,
        _ => false
    };

    public static string FromProcessingStage(string? stage) => stage switch
    {
        GradingProcessingStage.Ingestion => Acquisition,
        GradingProcessingStage.Context => Context,
        GradingProcessingStage.Analysis => Analysis,
        _ => Unknown
    };
}
