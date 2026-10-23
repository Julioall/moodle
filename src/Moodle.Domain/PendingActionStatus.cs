namespace Moodle.Domain;

public enum PendingActionStatus
{
    Draft = 0,
    PendingConfirmation = 1,
    Confirmed = 2,
    Executing = 3,
    Executed = 4,
    Expired = 5,
    Cancelled = 6,
    Failed = 7,
    /// <summary>
    /// The remote Moodle result cannot be determined. The action must not be
    /// submitted again without an explicit reconciliation workflow.
    /// </summary>
    ExecutionUnknown = 8,
    /// <summary>
    /// The operator authorized the action, but durable execution has not
    /// finished yet. This is intentionally distinct from confirmation so a
    /// crashed publisher can be resumed.
    /// </summary>
    Authorized = 9,
    /// <summary>
    /// Some publication items completed while others remain retryable. The
    /// action keeps its target claims and can be executed again.
    /// </summary>
    PartiallyCompleted = 10
}
