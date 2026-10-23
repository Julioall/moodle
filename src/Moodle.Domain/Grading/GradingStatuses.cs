namespace Moodle.Domain.Grading;

public enum GradingBatchStatus
{
    Pending = 0,
    Processing = 1,
    ReadyForReview = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}

public enum GradingItemStatus
{
    Pending = 0,
    Analyzing = 1,
    DraftReady = 2,
    ReadyToCommit = 3,
    Committed = 4,
    Blocked = 5,
    Failed = 6,
    AwaitingAiAnalysis = 7
}

public enum GradingReviewStatus
{
    NotReviewed = 0,
    Reviewed = 1,
    NeedsChanges = 2
}

public enum GradingCommitStatus
{
    NotReady = 0,
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
    /// <summary>
    /// Moodle may have applied the write even though transport failed. The
    /// item must be reconciled before any new submission is allowed.
    /// </summary>
    ExecutionUnknown = 4,
    /// <summary>A definite non-application is waiting for a bounded retry.</summary>
    RetryScheduled = 5
}
