using Moodle.Domain;
using Moodle.Domain.Registry;
using Moodle.Application.Registry;

namespace Moodle.Application.Tests.Registry;

public sealed class PolicyEngineTests
{
    private readonly PolicyEngine _sut = new();

    [Fact]
    public void Evaluate_ReturnsDeny_WhenOperationIsNull()
    {
        var result = _sut.Evaluate(null);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("not registered", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ReturnsDeny_WhenOperationIsBlocked()
    {
        var op = new MoodleOperation("blocked_op", "category", OperationType.Blocked, ToolRiskLevel.ReadOnly, OperationPolicy.Direct, "profile");

        var result = _sut.Evaluate(op);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("explicitly blocked", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ReturnsRedirect_WhenOperationIsControlledWrite()
    {
        var op = new MoodleOperation("write_op", "category", OperationType.ControlledWrite, ToolRiskLevel.DraftOnly, OperationPolicy.Direct, "profile");

        var result = _sut.Evaluate(op);

        Assert.Equal(PolicyDecision.RedirectToControlledWrite, result.Decision);
        Assert.Contains("controlled write", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ReturnsDeny_WhenOperationIsHighRiskRead()
    {
        var op = new MoodleOperation("high_risk_read", "category", OperationType.Read, ToolRiskLevel.SensitiveRead, OperationPolicy.Direct, "profile");

        var result = _sut.Evaluate(op);

        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.Contains("high-risk read", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ToolRiskLevel.ReadOnly)]
    [InlineData(ToolRiskLevel.DraftOnly)]
    public void Evaluate_ReturnsAllow_WhenOperationIsSafeRead(ToolRiskLevel riskLevel)
    {
        var op = new MoodleOperation("safe_read", "category", OperationType.Read, riskLevel, OperationPolicy.Direct, "profile");

        var result = _sut.Evaluate(op);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
    }
}
