namespace Moodle.Domain.Registry;

public enum OperationType
{
    /// <summary>
    /// Safe to execute via the Safe READ Executor.
    /// </summary>
    Read = 0,

    /// <summary>
    /// Requires a specialized workflow, usually involving PendingActions and user confirmation.
    /// </summary>
    ControlledWrite = 1,

    /// <summary>
    /// Strictly blocked from execution by agents.
    /// </summary>
    Blocked = 2
}
