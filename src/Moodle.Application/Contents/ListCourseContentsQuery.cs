using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Application.Contents;

public sealed record ListCourseContentsQuery(
    string UserExternalId,
    string CourseId,
    IReadOnlyCollection<string> ModuleTypes,
    bool IncludeHidden,
    bool OnlyWithFiles) : IRequest<CourseContentsSummary?>;

public sealed record GetCourseModuleQuery(
    string UserExternalId,
    string CourseId,
    string ModuleId) : IRequest<CourseModuleSummary?>;

public sealed record AuditCourseStructureQuery(
    string UserExternalId,
    string CourseId,
    bool IncludeHidden) : IRequest<CourseStructureAuditSummary?>;

public sealed class ListCourseContentsQueryHandler(
    IMoodleCoursesGateway coursesGateway,
    IMoodleCourseContentsGateway contentsGateway)
    : IRequestHandler<ListCourseContentsQuery, CourseContentsSummary?>
{
    public async Task<CourseContentsSummary?> Handle(
        ListCourseContentsQuery request,
        CancellationToken cancellationToken)
    {
        var course = await coursesGateway.GetMyCourseAsync(
            request.UserExternalId,
            request.CourseId,
            cancellationToken);
        if (course is null)
        {
            return null;
        }

        return await contentsGateway.GetCourseContentsAsync(
            request.UserExternalId,
            course.CourseId,
            NormalizeModuleTypes(request.ModuleTypes),
            request.IncludeHidden,
            request.OnlyWithFiles,
            cancellationToken);
    }

    internal static IReadOnlyCollection<string> NormalizeModuleTypes(IReadOnlyCollection<string> moduleTypes)
    {
        return moduleTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed class GetCourseModuleQueryHandler(
    IMoodleCoursesGateway coursesGateway,
    IMoodleCourseContentsGateway contentsGateway)
    : IRequestHandler<GetCourseModuleQuery, CourseModuleSummary?>
{
    public async Task<CourseModuleSummary?> Handle(
        GetCourseModuleQuery request,
        CancellationToken cancellationToken)
    {
        var course = await coursesGateway.GetMyCourseAsync(
            request.UserExternalId,
            request.CourseId,
            cancellationToken);
        if (course is null || string.IsNullOrWhiteSpace(request.ModuleId))
        {
            return null;
        }

        var contents = await contentsGateway.GetCourseContentsAsync(
            request.UserExternalId,
            course.CourseId,
            [],
            includeHidden: true,
            onlyWithFiles: false,
            cancellationToken);

        var normalizedModuleId = request.ModuleId.Trim();
        return contents.Sections
            .SelectMany(section => section.Modules)
            .FirstOrDefault(module =>
                string.Equals(module.ModuleId, normalizedModuleId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(module.InstanceId, normalizedModuleId, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class AuditCourseStructureQueryHandler(
    IMoodleCoursesGateway coursesGateway,
    IMoodleCourseContentsGateway contentsGateway)
    : IRequestHandler<AuditCourseStructureQuery, CourseStructureAuditSummary?>
{
    public async Task<CourseStructureAuditSummary?> Handle(
        AuditCourseStructureQuery request,
        CancellationToken cancellationToken)
    {
        var course = await coursesGateway.GetMyCourseAsync(
            request.UserExternalId,
            request.CourseId,
            cancellationToken);
        if (course is null)
        {
            return null;
        }

        var contents = await contentsGateway.GetCourseContentsAsync(
            request.UserExternalId,
            course.CourseId,
            [],
            request.IncludeHidden,
            onlyWithFiles: false,
            cancellationToken);

        return BuildAudit(contents);
    }

    private static CourseStructureAuditSummary BuildAudit(CourseContentsSummary contents)
    {
        var findings = new List<CourseStructureFinding>();
        var modules = contents.Sections.SelectMany(section => section.Modules).ToArray();

        foreach (var section in contents.Sections.Where(section => section.IsEmpty))
        {
            findings.Add(new CourseStructureFinding(
                "empty_section",
                "info",
                $"Secao sem modulos: {section.Name}.",
                section.SectionId,
                ModuleId: null,
                ModuleType: null));
        }

        foreach (var section in contents.Sections)
        {
            foreach (var module in section.Modules)
            {
                if (string.IsNullOrWhiteSpace(module.Description))
                {
                    findings.Add(new CourseStructureFinding(
                        "module_without_description",
                        "warning",
                        $"Modulo sem descricao: {module.Name}.",
                        section.SectionId,
                        module.ModuleId,
                        module.ModuleType));
                }

                if (module.Dates.Count == 0)
                {
                    findings.Add(new CourseStructureFinding(
                        "module_without_dates",
                        "info",
                        $"Modulo sem datas retornadas pelo Moodle: {module.Name}.",
                        section.SectionId,
                        module.ModuleId,
                        module.ModuleType));
                }
            }
        }

        return new CourseStructureAuditSummary(
            contents.CourseId,
            contents.Sections.Count,
            modules.Length,
            contents.Sections.Count(section => section.IsEmpty),
            modules.Count(module => string.IsNullOrWhiteSpace(module.Description)),
            modules.Count(module => module.Dates.Count == 0),
            findings);
    }
}
