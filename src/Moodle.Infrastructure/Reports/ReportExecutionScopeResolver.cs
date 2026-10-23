using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Application.Abstractions.Reports;
using Moodle.Application.Reports.Templates;

namespace Moodle.Infrastructure.Reports;

public sealed class ReportExecutionScopeResolver(ConnectorDbContext db, IPlatformPermissionService permissions,
    IMoodleCredentialsProvider credentials, IMoodleCurrentUserIdGateway currentUser,
    IMoodleCoursesGateway courses, IMoodleConnectionSelection selection) : IReportExecutionScopeResolver
{
    public async Task<ReportExecutionScope> ResolveAsync(Guid ownerId, string connectionRef,
        IReadOnlyCollection<string> courseIds, CancellationToken ct)
    {
        if (!(await permissions.GetEffectivePermissionsAsync(ownerId, ct)).Contains("reports.view", StringComparer.Ordinal))
            throw new ReportDatasetException("report_permission_denied");
        var clientId = await db.UserAccounts.AsNoTracking().Where(a => a.Id == ownerId)
            .Select(a => a.ConnectorClientId).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(connectionRef))
            throw new ReportDatasetException("connection_unavailable");
        var ids = ReportDatasetResolver.NormalizeCourses(courseIds);
        var previousAlias = selection.Alias;
        try
        {
            selection.Alias = connectionRef;
            var connectionId = await MoodleConnectionIdentity.ResolveAsync(db, ownerId, clientId, connectionRef, ct);
            if (connectionId.StartsWith("legacy-", StringComparison.Ordinal)) throw new ReportDatasetException("connection_unavailable");
            var active = await credentials.GetCurrentCredentialsAsync(ct);
            if (active.ConnectionId != connectionId) throw new ReportDatasetException("connection_changed");
            var userId = (await currentUser.GetCurrentUserIdAsync(ct)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var accessible = await courses.GetReportCoursesLiveAsync(userId, ids, false, false, ct);
            if (ids.Any(id => !accessible.Any(c => c.CourseId == id))) throw new ReportDatasetException("course_unavailable");
            return new(ownerId, clientId, connectionRef.Trim(), connectionId, ids);
        }
        finally { selection.Alias = previousAlias; }
    }
}
