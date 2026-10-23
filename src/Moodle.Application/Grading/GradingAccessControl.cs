using Moodle.Application.Abstractions;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

internal static class GradingAccessControl
{
    private const string UnauthorizedMessage = "Usuario atual nao esta autorizado a acessar este lote de correcao.";

    public static void EnsureCanAccessBatch(AssistedGradingBatch batch, ICurrentUserContext currentUser)
    {
        if (string.Equals(batch.CreatedBySubject, currentUser.Subject, StringComparison.Ordinal))
        {
            return;
        }

        if (currentUser.HasScope("grading.admin") || currentUser.HasPlatformPermission("tool.assignments.grade"))
        {
            return;
        }

        throw new UnauthorizedAccessException(UnauthorizedMessage);
    }
}
