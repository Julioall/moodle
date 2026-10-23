namespace Moodle.Application.Abstractions;

public interface IMoodleCallTelemetry
{
    void RecordMoodleWebServiceCall(string? connectionAlias = null);

    void RecordMoodleWebServiceCall(string? connectionAlias, string? functionName) =>
        RecordMoodleWebServiceCall(connectionAlias);

    void RecordMoodleWebServiceCall(string? connectionAlias, string? functionName, string? operationKind) =>
        RecordMoodleWebServiceCall(connectionAlias, functionName);

    void RecordMoodleWebServiceCompleted(string? connectionAlias, string? functionName, double durationMs)
    {
    }

    void RecordMoodleWebServiceCompleted(
        string? connectionAlias,
        string? functionName,
        string? operationKind,
        int? httpStatusCode,
        double durationMs,
        long requestPayloadBytes,
        long responsePayloadBytes) =>
        RecordMoodleWebServiceCompleted(connectionAlias, functionName, durationMs);

    void RecordMoodleWebServiceTransportAttempt(
        string? connectionAlias,
        string? functionName,
        string? operationKind,
        int attempt)
    {
    }

    void RecordMoodleDiagnosticWrite(string outcome, int eventCount)
    {
    }

    void RecordMoodleDiagnosticQueueDepth(int queuedGroups)
    {
    }

    void RecordMoodleDiagnosticQueueWait(double waitDurationMs)
    {
    }

    void RecordMoodleDiagnosticCleanup(string outcome, int rowsDeleted)
    {
    }

    void RecordMoodleDiagnosticStdoutDrop(int eventCount)
    {
    }

    void RecordMoodleWebServiceLogicalRetry(
        string? connectionAlias,
        string? functionName,
        string? toolName,
        string? canonicalOperation,
        int attempt)
    {
    }

    void RecordMoodleReadGateState(
        string? connectionAlias,
        string? functionName,
        string? toolName,
        string? canonicalOperation,
        string outcome,
        int active,
        int queued,
        double waitDurationMs)
    {
    }

    void RecordForumPostReadBatch(
        string? toolName,
        string? canonicalOperation,
        int discussions,
        int postReadCalls,
        int failedDiscussions,
        int maxConcurrentPosts,
        double durationMs)
    {
    }

    void RecordScormParse(
        string? toolName,
        string? canonicalOperation,
        int contentEntryReads,
        long selectedUncompressedBytes,
        long allocatedBytes,
        double durationMs,
        int scoCount,
        int fileCount,
        bool succeeded)
    {
    }

    void RecordMoodleWebServiceFailure(string? connectionAlias, string? functionName, string? errorCode, double durationMs)
    {
    }

    void RecordMoodleWebServiceFailure(
        string? connectionAlias,
        string? functionName,
        string? errorCode,
        double durationMs,
        int? httpStatusCode) =>
        RecordMoodleWebServiceFailure(connectionAlias, functionName, errorCode, durationMs);

    void RecordMoodleWebServiceFailure(
        string? connectionAlias,
        string? functionName,
        string? errorCode,
        string? operationKind,
        double durationMs,
        int? httpStatusCode,
        long requestPayloadBytes,
        long responsePayloadBytes) =>
        RecordMoodleWebServiceFailure(connectionAlias, functionName, errorCode, durationMs, httpStatusCode);
}
