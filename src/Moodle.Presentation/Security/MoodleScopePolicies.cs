using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Moodle.Presentation.Security;

public static class MoodleScopePolicies
{
    public const string PortalSession = "portal.session";
    // Coarse scopes are reserved for universal connector primitives whose
    // remote operation is selected dynamically at runtime.
    public const string ReadAny = "moodle.read";
    public const string WriteAny = "moodle.write";
    public const string ReadCourses = "moodle.read.courses";
    public const string ReadStudents = "moodle.read.students";
    public const string ReadGroups = "moodle.read.groups";
    public const string ReadAccess = "moodle.read.access";
    public const string ReadContents = "moodle.read.contents";
    public const string ReadResources = "moodle.read.resources";
    public const string ReadActivities = "moodle.read.activities";
    public const string ReadAssignments = "moodle.read.assignments";
    public const string ReadSubmissions = "moodle.read.submissions";
    public const string ReadQuizzes = "moodle.read.quizzes";
    public const string ReadScorms = "moodle.read.scorms";
    public const string ReadForums = "moodle.read.forums";
    public const string WriteMessages = "moodle.write.messages";
    public const string WriteAssignmentsFeedback = "moodle.write.assignments.feedback";
    public const string WriteAssignmentsGrade = "moodle.write.assignments.grade";
    public const string WriteCourseContent = "moodle.write.course_content";
    public const string WriteForums = "moodle.write.forums";

    public static void AddMoodleScopePolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(PortalSession, policy => policy
            .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser());
        options.AddPolicy(ReadCourses, policy => policy.RequireAssertion(context => HasScope(context, ReadCourses)));
        options.AddPolicy(ReadStudents, policy => policy.RequireAssertion(context => HasScope(context, ReadStudents)));
        options.AddPolicy(ReadGroups, policy => policy.RequireAssertion(context => HasScope(context, ReadGroups)));
        options.AddPolicy(ReadAccess, policy => policy.RequireAssertion(context => HasScope(context, ReadAccess)));
        options.AddPolicy(ReadContents, policy => policy.RequireAssertion(context => HasScope(context, ReadContents)));
        options.AddPolicy(ReadResources, policy => policy.RequireAssertion(context => HasScope(context, ReadResources)));
        options.AddPolicy(ReadActivities, policy => policy.RequireAssertion(context => HasScope(context, ReadActivities)));
        options.AddPolicy(ReadAssignments, policy => policy.RequireAssertion(context => HasScope(context, ReadAssignments)));
        options.AddPolicy(ReadSubmissions, policy => policy.RequireAssertion(context => HasScope(context, ReadSubmissions)));
        options.AddPolicy(ReadQuizzes, policy => policy.RequireAssertion(context => HasScope(context, ReadQuizzes)));
        options.AddPolicy(ReadScorms, policy => policy.RequireAssertion(context => HasScope(context, ReadScorms)));
        options.AddPolicy(ReadForums, policy => policy.RequireAssertion(context => HasScope(context, ReadForums)));
        options.AddPolicy(WriteMessages, policy => policy.RequireAssertion(context => HasScope(context, WriteMessages)));
        options.AddPolicy(WriteAssignmentsFeedback, policy => policy.RequireAssertion(context => HasScope(context, WriteAssignmentsFeedback)));
        options.AddPolicy(WriteAssignmentsGrade, policy => policy.RequireAssertion(context => HasScope(context, WriteAssignmentsGrade)));
        options.AddPolicy(WriteCourseContent, policy => policy.RequireAssertion(context => HasScope(context, WriteCourseContent)));
        options.AddPolicy(WriteForums, policy => policy.RequireAssertion(context => HasScope(context, WriteForums)));
    }

    private static bool HasScope(AuthorizationHandlerContext context, string scope)
    {
        return context.User.FindAll("scope")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(value => string.Equals(value, scope, StringComparison.OrdinalIgnoreCase));
    }
}
