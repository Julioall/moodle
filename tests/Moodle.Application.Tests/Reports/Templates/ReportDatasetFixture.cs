using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Reports.Templates;
using Moodle.Application.Tests.Infrastructure;
using Moodle.Domain;
using Moodle.Domain.Reports;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Reports.Templates;

internal sealed class ReportDatasetFixture : IDisposable
{
    public readonly Guid Owner = Guid.NewGuid();
    public readonly ReportTransportFixture Transport = new();
    public readonly MemoryCache Cache = new(new MemoryCacheOptions());
    public readonly ColdSnapshots Snapshots = new();
    public readonly CourseGateway Courses = new();
    public readonly ReportTemplateOptions Limits = new();
    public readonly Dictionary<string, int[]> Population = new(StringComparer.Ordinal)
        { ["1"] = Enumerable.Range(1, 20).ToArray(), ["2"] = Enumerable.Range(1, 20).ToArray(), ["3"] = [1] };
    public decimal? GradeValue = 25;
    public bool GradeMaxAvailable = true;
    public long CourseAccess = 0;
    public bool FailGrade;
    public bool FailPopulation;
    public string ParticipantRoleShortName = "student";
    public string ParticipantRoleName = "Student";
    public ReportDatasetFixture()
    {
        Snapshots.Owner = Owner;
        Transport.Response = (function, p) =>
        {
            if (function == "core_enrol_get_enrolled_users")
            {
                if (FailPopulation) return ReportTransportFixture.Json("""{"errorcode":"nopermissions","exception":"required_capability_exception","message":"Synthetic denial"}""");
                var ids = Population[p["courseid"]];
                if (p["options[0][value]"] != "0") ids = [];
                return ReportTransportFixture.Json(JsonSerializer.Serialize(ids.Select(id => new
                { id, fullname = $"Live synthetic {id}", suspended = false, lastaccess = 1700000000, lastcourseaccess = CourseAccess,
                    roles = new[] { new { roleid = 5, shortname = ParticipantRoleShortName, name = ParticipantRoleName } }, groups = Array.Empty<object>() })));
            }
            if (function == "gradereport_user_get_grade_items")
            {
                if (FailGrade) return ReportTransportFixture.Json("{}", System.Net.HttpStatusCode.InternalServerError);
                var ids = p["userid"] == "0" ? Population[p["courseid"]] : [int.Parse(p["userid"])];
                return ReportTransportFixture.Json(JsonSerializer.Serialize(new { usergrades = ids.Select(id => new
                { userid = id, gradeitems = new[] { new { id = 99, itemtype = "course", graderaw = GradeValue,
                    grademax = GradeMaxAvailable ? (decimal?)100 : null } } }) }));
            }
            throw new InvalidOperationException("Unexpected hidden REST read.");
        };
    }
    public ReportDatasetResolver Resolver() => new(new MoodleParticipantsGateway(Options.Create(new MoodleApiOptions()), Transport.Credentials, Transport.Rest),
        new MoodleGradebookGateway(Options.Create(new MoodleApiOptions()), Transport.Credentials, Transport.Rest, Cache),
        Courses, Transport.Credentials, new User(), new(), new(new(), Options.Create(Limits)), Options.Create(Limits), TimeProvider.System, Snapshots);
    public ReportDatasetRequest Request(bool preview = false, params string[] fields) => new(Owner, "synthetic", "synthetic-connection",
        ["1", "2", "3"], new(1, "student_course", fields.Select(f => new ReportColumnDefinition(f)).ToArray()), preview);
    public void Dispose() { Cache.Dispose(); Transport.Dispose(); }
    public static CourseParticipantSummary Student(string id, string name) =>
        new(id, name, null, false, null, DateTimeOffset.UtcNow.AddYears(-5), DateTimeOffset.UtcNow.AddYears(-5), [], [], "active");
    private sealed class User : IMoodleCurrentUserIdGateway
    { public Task<long> GetCurrentUserIdAsync(CancellationToken ct) => Task.FromResult(100L); }
    public sealed class CourseGateway : IMoodleCoursesGateway
    {
        public bool SeparateCategories;
        public HashSet<string> Inaccessible { get; } = [];
        public string[]? ReadIds;
        public bool NeedsCategories;
        public Task<IReadOnlyList<CourseSummary>> GetReportCoursesLiveAsync(string user, IReadOnlyCollection<string> ids,
            bool categories, bool singleAttempt, CancellationToken ct)
        {
            ReadIds = ids.ToArray(); NeedsCategories = categories;
            return Task.FromResult<IReadOnlyList<CourseSummary>>(ids.Where(id => !Inaccessible.Contains(id)).Select(id => new CourseSummary(id, null, null,
                $"Live course {id}", null, SeparateCategories ? int.Parse(id) : 1, "Synthetic > Category", null, null, true, null, null, null, null, null, null)).ToArray());
        }
        private static CourseSummary BrowserCourse(int id) => new(id.ToString(), null, null, $"Synthetic UC {id:000}", null, id <= 110 ? 1 : 2,
            id <= 110 ? "Root > Catalog Alfa" : "Root > Catalog Beta", null, null, true, null, null, null, null, null, null);
        public Task<PagedCourses> GetMyCoursesAsync(string user, int limit, int page, CancellationToken ct) =>
            Task.FromResult(new PagedCourses(Enumerable.Range(1, 112).Skip((page - 1) * limit).Take(limit).Select(BrowserCourse).ToArray(), 112, page, limit, CourseReadSource.Moodle));
        public Task<IReadOnlyList<CourseSummary>> SearchMyCoursesAsync(string user, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CourseSummary>>(Enumerable.Range(1, 112).Select(BrowserCourse).Where(item => item.FullName.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(limit).ToArray());
        public Task<CourseSummary?> GetMyCourseAsync(string user, string id, CancellationToken ct) => Task.FromResult<CourseSummary?>(BrowserCourse(int.Parse(id)));
        public Task<IReadOnlyList<CourseHierarchyNode>> GetMyCourseHierarchyAsync(string user, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CourseHierarchyNode>>([new("Root > Catalog Alfa", "Catalog Alfa", 1, 110), new("Root > Catalog Beta", "Catalog Beta", 1, 2)]);
        public Task<PagedCourses> GetMyCoursesByCategoryAsync(string user, string path, int limit, int page, CancellationToken ct)
        {
            var courses = Enumerable.Range(1, 112).Select(BrowserCourse).Where(item => item.CategoryName == path).ToArray();
            return Task.FromResult(new PagedCourses(courses.Skip((page - 1) * limit).Take(limit).ToArray(), courses.Length, page, limit, CourseReadSource.Moodle));
        }
    }
    public sealed class ColdSnapshots : IMoodleSnapshotStore
    {
        public Guid Owner;
        public readonly Dictionary<(string Dataset, string Course), object> Data = [];
        public readonly List<string> Reads = [];
        public Task<MoodleSnapshotEnvelope<T>?> GetReportSnapshotAsync<T>(Guid owner, string connection, string dataset, string course, CancellationToken ct)
        {
            Reads.Add(dataset);
            return Task.FromResult(owner == Owner && connection == "synthetic-connection" && Data.TryGetValue((dataset, course), out var value)
                ? (MoodleSnapshotEnvelope<T>)value : null);
        }
        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?> GetCoursesAsync(Guid owner, string alias, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MoodleSnapshotEnvelope<CourseContentsSummary>?> GetActivitiesAsync(Guid owner, string alias, string course, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MoodleSnapshotEnvelope<CourseParticipantsPage>?> GetStudentsAsync(Guid owner, string alias, string course, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?> GetGroupsAsync(Guid owner, string alias, string course, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MoodleSnapshotEnvelope<T>?> GetAsync<T>(Guid owner, string alias, string dataset, string course = "", CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveAsync<T>(Guid owner, string alias, string dataset, string course, T payload, string tier, bool frozen,
            bool complete, int count, DateTimeOffset now, CancellationToken ct = default) => throw new InvalidOperationException("Preview must not persist.");
        public void Invalidate(Guid owner, string alias, string dataset, string course = "") => throw new InvalidOperationException("No implicit refresh.");
    }
}
