namespace Moodle.Domain.Registry;

public enum PolicyDecision
{
    Allow,
    RedirectToControlledWrite,
    Deny
}

public sealed record PolicyEvaluationResult(
    PolicyDecision Decision,
    string Reason
);
