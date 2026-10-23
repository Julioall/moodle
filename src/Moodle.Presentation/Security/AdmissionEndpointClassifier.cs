using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Security;

/// <summary>
/// Inventory-backed classification for the currently registered HTTP
/// endpoints. Every method/template pair is explicit. The integration test
/// compares this inventory with EndpointDataSource so newly mapped routes
/// cannot silently inherit a guessed policy.
/// </summary>
internal static class AdmissionEndpointClassifier
{
    internal const string StaticAssetExemptionReason =
        "Static assets are served without application or Moodle work and remain subject to edge-level volume controls.";

    private static readonly IReadOnlyDictionary<RouteKey, AdmissionRouteMetadata> Classifications = Build();

    public static AdmissionRouteMetadata? Classify(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.Metadata.GetMetadata<AdmissionRouteMetadata>() is { } metadata)
        {
            return metadata;
        }

        if (endpoint is not RouteEndpoint route || route.RoutePattern.RawText is not { } template)
        {
            return null;
        }

        var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        if (methods is null || methods.Count == 0)
        {
            return Classifications.GetValueOrDefault(new RouteKey("*", template));
        }

        AdmissionRouteMetadata? result = null;
        foreach (var method in methods)
        {
            if (!Classifications.TryGetValue(new RouteKey(method, template), out var current))
            {
                return null;
            }

            if (result is not null && result != current)
            {
                return null;
            }

            result = current;
        }

        return result;
    }

    private static IReadOnlyDictionary<RouteKey, AdmissionRouteMetadata> Build()
    {
        var routes = new Dictionary<RouteKey, AdmissionRouteMetadata>();

        AddExempt(routes, AdmissionTrafficClass.Operations, "health",
            "Health probes must remain available independently of per-owner request quotas.",
            "* /health", "* /health/live");
        Add(routes, AdmissionTrafficClass.OAuthProtocol, "oauth_discovery",
            "GET /.well-known/oauth-authorization-server",
            "GET /.well-known/oauth-protected-resource",
            "GET /.well-known/oauth-protected-resource/{**resourcePath}",
            "GET,POST /authorize",
            "POST /register");
        Add(routes, AdmissionTrafficClass.McpTransport, "mcp_transport",
            "DELETE /mcp/", "GET /mcp/", "POST /mcp/");
        AddExempt(routes, AdmissionTrafficClass.Operations, "operational_status",
            "Status and build information are low-cost operational endpoints with separate edge controls.",
            "GET /api/status", "GET /api/info");
        AddExempt(routes, AdmissionTrafficClass.StaticContent, "static_shell",
            StaticAssetExemptionReason,
            "GET /app.html", "GET /auth.html");
        AddExempt(routes, AdmissionTrafficClass.StaticContent, "static_fallback",
            StaticAssetExemptionReason,
            "GET,HEAD /{*path:nonfile}");
        Add(routes, AdmissionTrafficClass.PublicAuthentication, "authentication",
            "GET /auth/login", "POST /auth/login", "POST /api/account/login", "POST /api/account/register");
        Add(routes, AdmissionTrafficClass.SessionControl, "session_control",
            "POST /auth/logout", "GET /api/account/me", "GET /api/csrf", "GET /api/session");

        Add(routes, AdmissionTrafficClass.Administration, "admin_api",
            "POST /admin/connector-clients/register",
            "GET /api/admin/accounts",
            "GET /api/admin/metrics",
            "POST /api/admin/accounts/delete",
            "POST /api/admin/accounts/{userId:guid}/reset-password",
            "GET /api/permission-catalog",
            "GET /api/permission-groups",
            "POST /api/permission-groups",
            "POST /api/permission-groups/{groupId:guid}/members",
            "PUT /api/permission-groups/{groupId:guid}",
            "PUT /api/users/{userId:guid}/platform-permissions");
        Add(routes, AdmissionTrafficClass.SensitiveOperation, "account_security",
            "POST /api/account/password",
            "POST /api/account/api-key/rotate",
            "DELETE /api/account");
        Add(routes, AdmissionTrafficClass.SensitiveOperation, "moodle_connections",
            "POST /api/account/connect-moodle",
            "PUT /api/account/moodle/{id}",
            "DELETE /api/account/moodle/{id}",
            "POST /api/connections",
            "PUT /api/connections/{id}",
            "POST /api/connections/{id}/validate",
            "DELETE /api/connections/{id}");

        Add(routes, AdmissionTrafficClass.SessionControl, "connection_session",
            "GET /api/connections");
        Add(routes, AdmissionTrafficClass.LocalRead, "connection_status",
            "GET /api/connections/{id}/data-summary");
        Add(routes, AdmissionTrafficClass.LocalRead, "course_catalog",
            "GET /api/schools",
            "GET /api/schools/courses",
            "GET /api/courses/search",
            "GET /api/courses");
        Add(routes, AdmissionTrafficClass.LocalRead, "course_preferences",
            "GET /api/course-preferences/ignored",
            "GET /api/course-preferences/tracked");
        Add(routes, AdmissionTrafficClass.LocalMutation, "course_preferences",
            "PUT /api/course-preferences/ignored",
            "PUT /api/course-preferences/tracked");
        Add(routes, AdmissionTrafficClass.RemoteRead, "course_details",
            "GET /api/courses/{connectionRef}/{courseId}",
            "GET /api/courses/{connectionRef}/{courseId}/activities",
            "GET /api/courses/{connectionRef}/{courseId}/forums",
            "GET /api/courses/{connectionRef}/{courseId}/forums/{forumId}",
            "GET /api/courses/{connectionRef}/{courseId}/overview",
            "GET /api/courses/{connectionRef}/{courseId}/students",
            "GET /api/courses/{connectionRef}/{courseId}/students/{studentId}");
        Add(routes, AdmissionTrafficClass.RemoteRead, "dashboard",
            "GET /api/dashboard", "GET /api/dashboard/{metric}");
        Add(routes, AdmissionTrafficClass.RemoteRead, "submissions",
            "GET /api/pending",
            "GET /api/submissions",
            "GET /api/submissions/{courseId}/{assignmentId}/{studentId}");
        Add(routes, AdmissionTrafficClass.LocalRead, "evidence", "GET /api/evidence");
        Add(routes, AdmissionTrafficClass.RemoteRead, "messages",
            "GET /api/messages/conversations",
            "GET /api/messages/conversations/{moodleUserId:long}");
        Add(routes, AdmissionTrafficClass.Preview, "message_preparation",
            "POST /api/messages/prepare",
            "POST /api/messages/conversations/{moodleUserId:long}/prepare");
        Add(routes, AdmissionTrafficClass.SensitiveOperation, "message_confirmation",
            "POST /api/messages/confirm");

        Add(routes, AdmissionTrafficClass.LocalRead, "schools_followups",
            "GET /api/followups");
        Add(routes, AdmissionTrafficClass.LocalMutation, "schools_followups",
            "POST /api/followups");
        Add(routes, AdmissionTrafficClass.LocalRead, "agenda",
            "GET /api/agenda", "GET /api/agenda/export.ics", "GET /api/agenda/occurrences",
            "GET /api/agenda/{id:guid}", "GET /api/agenda/{id:guid}/tasks");
        Add(routes, AdmissionTrafficClass.LocalMutation, "agenda",
            "POST /api/agenda", "POST /api/agenda/import", "PATCH /api/agenda/{id:guid}",
            "POST /api/agenda/professional", "PATCH /api/agenda/{id:guid}/professional",
            "POST /api/agenda/{id:guid}/tasks", "PUT /api/agenda/{id:guid}/occurrences/{originalStart}",
            "PATCH /api/agenda/{id:guid}/occurrences/{originalStart}",
            "DELETE /api/agenda/{id:guid}", "DELETE /api/agenda/{id:guid}/occurrences/{originalStart}");
        Add(routes, AdmissionTrafficClass.LocalRead, "planner_history", "GET /api/planner/history");

        Add(routes, AdmissionTrafficClass.LocalRead, "tasks",
            "GET /api/tasks", "GET /api/tasks/{id:guid}", "GET /api/tasks/{id:guid}/activity",
            "GET /api/tasks/{id:guid}/dependencies", "GET /api/tasks/{id:guid}/events",
            "GET /api/tasks/{id:guid}/subtasks");
        Add(routes, AdmissionTrafficClass.LocalMutation, "tasks",
            "POST /api/tasks", "PATCH /api/tasks/{id:guid}", "DELETE /api/tasks", "DELETE /api/tasks/{id:guid}",
            "DELETE /api/tasks/{id:guid}/dependencies/{dependsOnId:guid}",
            "DELETE /api/tasks/{id:guid}/events/{linkId:guid}",
            "DELETE /api/tasks/{id:guid}/participants/{userId:guid}",
            "DELETE /api/tasks/{id:guid}/references/{referenceId:guid}",
            "DELETE /api/tasks/{id:guid}/tags/{value}",
            "PATCH /api/tasks/{id:guid}/comments/{commentId:guid}",
            "POST /api/tasks/{id:guid}/comments", "POST /api/tasks/{id:guid}/complete",
            "POST /api/tasks/{id:guid}/dependencies", "POST /api/tasks/{id:guid}/events",
            "POST /api/tasks/{id:guid}/events/create", "POST /api/tasks/{id:guid}/participants",
            "POST /api/tasks/{id:guid}/references", "POST /api/tasks/{id:guid}/reopen",
            "POST /api/tasks/{id:guid}/subtasks", "POST /api/tasks/{id:guid}/tags");

        Add(routes, AdmissionTrafficClass.LocalRead, "teams",
            "GET /api/teams");
        Add(routes, AdmissionTrafficClass.SensitiveOperation, "teams",
            "POST /api/teams/{teamId:guid}/invitations", "POST /api/team-invitations/accept");

        Add(routes, AdmissionTrafficClass.LocalRead, "reports",
            "GET /api/reports/audit", "GET /api/reports/catalog", "GET /api/reports/operational",
            "GET /api/reports/templates/",
            "GET /api/reports/templates/{id:guid}", "GET /api/reports/jobs", "GET /api/reports/jobs/{id:guid}");
        Add(routes, AdmissionTrafficClass.RemoteRead, "report_builder", "GET /api/reports/student-course");
        Add(routes, AdmissionTrafficClass.RemoteRead, "report_overviews",
            "GET /api/reports/completion/{connectionRef}/{courseId}",
            "GET /api/reports/course-overview/{connectionRef}/{courseId}",
            "GET /api/reports/weekly/{connectionRef}/{courseId}");
        Add(routes, AdmissionTrafficClass.Download, "report_downloads",
            "GET /api/reports/jobs/{id:guid}/download");
        Add(routes, AdmissionTrafficClass.JobSubmission, "report_jobs",
            "POST /api/reports/jobs");
        Add(routes, AdmissionTrafficClass.Preview, "report_preview",
            "POST /api/reports/preview");
        Add(routes, AdmissionTrafficClass.LocalMutation, "report_templates",
            "POST /api/reports/templates/", "PUT /api/reports/templates/{id:guid}",
            "DELETE /api/reports/templates/{id:guid}", "DELETE /api/reports/jobs/{id:guid}");

        return routes;
    }

    private static void Add(
        IDictionary<RouteKey, AdmissionRouteMetadata> routes,
        AdmissionTrafficClass trafficClass,
        string family,
        params string[] methodAndTemplates) =>
        AddWithReason(routes, trafficClass, family, null, methodAndTemplates);

    private static void AddExempt(
        IDictionary<RouteKey, AdmissionRouteMetadata> routes,
        AdmissionTrafficClass trafficClass,
        string family,
        string reason,
        params string[] methodAndTemplates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        AddWithReason(routes, trafficClass, family, reason, methodAndTemplates);
    }

    private static void AddWithReason(
        IDictionary<RouteKey, AdmissionRouteMetadata> routes,
        AdmissionTrafficClass trafficClass,
        string family,
        string? exemptionReason,
        params string[] methodAndTemplates)
    {
        var metadata = new AdmissionRouteMetadata(trafficClass, family, exemptionReason);
        foreach (var route in methodAndTemplates)
        {
            var separator = route.IndexOf(' ');
            if (separator <= 0 || separator == route.Length - 1)
            {
                throw new InvalidOperationException($"Invalid admission route entry: {route}");
            }

            var methods = route[..separator].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var template = route[(separator + 1)..];
            foreach (var method in methods)
            {
                var key = new RouteKey(method, template);
                if (!routes.TryAdd(key, metadata))
                {
                    throw new InvalidOperationException($"Duplicate admission route entry: {method} {template}");
                }
            }
        }
    }

    private readonly record struct RouteKey(string Method, string Template);
}
