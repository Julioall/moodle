using Moodle.Application.Tools;

namespace Moodle.Application.PendingActions;

public interface IActionConfirmationService
{
    Task<ActionConfirmationResponse> ConfirmAsync(
        Guid pendingActionId,
        string? confirmationText,
        string? requiredScope,
        CancellationToken cancellationToken);
}
