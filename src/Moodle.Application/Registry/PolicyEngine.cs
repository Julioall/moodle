using Moodle.Domain;
using Moodle.Domain.Registry;

namespace Moodle.Application.Registry;

public sealed class PolicyEngine : IPolicyEngine
{
    public PolicyEvaluationResult Evaluate(MoodleOperation? operation)
    {
        if (operation is null)
        {
            return new PolicyEvaluationResult(PolicyDecision.Deny, "Operation is not registered in the Operation Registry.");
        }

        if (operation.Type == OperationType.Blocked)
        {
            return new PolicyEvaluationResult(PolicyDecision.Deny, $"Operation '{operation.OperationName}' is explicitly blocked.");
        }

        if (operation.Type == OperationType.ControlledWrite)
        {
            return new PolicyEvaluationResult(PolicyDecision.RedirectToControlledWrite, $"Operation '{operation.OperationName}' is a controlled write and requires the pending action workflow.");
        }

        // Operation is Read
        if (operation.RiskLevel == ToolRiskLevel.SensitiveRead)
        {
            return new PolicyEvaluationResult(PolicyDecision.Deny, $"Operation '{operation.OperationName}' is a high-risk read and is not allowed via the generic executor.");
        }

        return new PolicyEvaluationResult(PolicyDecision.Allow, "Operation allowed.");
    }
}
