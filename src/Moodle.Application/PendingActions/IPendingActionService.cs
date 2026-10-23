using Moodle.Application.Tools;
using Moodle.Domain;

namespace Moodle.Application.PendingActions;

public interface IPendingActionService
{
    Task<PendingActionResponse> CreatePendingActionAsync(
        string toolName,
        ToolRiskLevel riskLevel,
        object payload,
        object preview,
        string confirmationText,
        TimeSpan expiresIn,
        long? courseId,
        CancellationToken cancellationToken);
}
