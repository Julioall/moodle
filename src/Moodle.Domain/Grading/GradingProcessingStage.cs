namespace Moodle.Domain.Grading;

public static class GradingProcessingStage
{
    public const string Pending = "pending";
    public const string Ingestion = "ingestion";
    public const string Context = "context";
    public const string Analysis = "analysis";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static bool IsKnown(string value) => value switch
    {
        Pending or Ingestion or Context or Analysis or Completed or Failed => true,
        _ => false
    };
}
