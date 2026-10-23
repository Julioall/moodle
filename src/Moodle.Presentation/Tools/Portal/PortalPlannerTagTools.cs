using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Presentation;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Tools.Portal;

[McpServerToolType]
public sealed class PortalPlannerTagTools(
    IMoodleCoursesGateway coursesGateway,
    IMoodleParticipantsGateway participantsGateway,
    IMoodleUserResolver moodleUserResolver,
    IMoodleConnectionSelection moodleSelection)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex TagPattern = new(@"(?<!\S)/(?:(?<explicit>escola|school|curso|course|turma|class|aluno|student)\s+(?<value>[^/]+)|(?<bare>[^\s/]+))", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    [McpServerTool(
        Name = "resolve_planner_tags",
        Title = "Resolver tags da agenda",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<PlannerTagResolutionResponse>))]
    [MoodleToolMetadata(
        Family = "portal-planner",
        Classification = "R1",
        Kind = "read",
        CanonicalOperation = "portal.planner.tags.resolve",
        RequiredPlatformPermission = "courses.view",
        Evidence = "Resolve tokens de agenda contra cursos, grupos/turmas, estudantes e categorias/escolas visíveis ao usuário no Moodle.")]
    [Description("Interpreta tags como /Joao, /escola X, /curso Matemática ou /turma A e retorna referências estruturadas. Se houver mais de um resultado, não escolha silenciosamente: apresente as opções para confirmação.")]
    public async Task<CallToolResult> ResolveAsync(
        [Description("Texto contendo uma ou mais tags no formato /Joao, /escola X, /curso Y ou /turma Z.")] string text,
        [Description("Alias do Moodle, quando necessário.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Informe o texto com as tags da agenda.", nameof(text));
            moodleSelection.Alias = moodleAlias;
            var userId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
            if (userId is null) return Error("Não foi possível resolver o usuário Moodle atual.");

            var tags = ParseTags(text);
            var results = new List<PlannerTagResolutionItem>(tags.Count);
            foreach (var tag in tags)
            {
                var candidates = tag.Type switch
                {
                    "course" => await ResolveCoursesAsync(userId.Value.ToString(), tag.Term, moodleAlias, cancellationToken),
                    "school" => await ResolveSchoolsAsync(userId.Value.ToString(), tag.Term, moodleAlias, cancellationToken),
                    "class" => await ResolveClassesAsync(userId.Value.ToString(), tag.Term, moodleAlias, cancellationToken),
                    _ => await ResolveStudentsAsync(userId.Value.ToString(), tag.Term, moodleAlias, cancellationToken)
                };
                var status = candidates.Count switch { 0 => "not_found", 1 => "resolved", _ => "ambiguous" };
                results.Add(new PlannerTagResolutionItem(tag.Token, tag.Type, tag.Term, status, candidates));
            }

            return Success(new PlannerTagResolutionResponse(text, results), $"{results.Count} tag(s) interpretada(s).");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Error(exception.Message);
        }
    }

    private async Task<IReadOnlyList<PlannerReferenceDto>> ResolveCoursesAsync(string userId, string term, string? connectionRef, CancellationToken cancellationToken)
    {
        var courses = await coursesGateway.SearchMyCoursesAsync(userId, term, 20, cancellationToken);
        return courses.Select(course => new PlannerReferenceDto("course", course.CourseId, course.FullName, connectionRef, null, null, null)).ToArray();
    }

    private async Task<IReadOnlyList<PlannerReferenceDto>> ResolveSchoolsAsync(string userId, string term, string? connectionRef, CancellationToken cancellationToken)
    {
        var nodes = await coursesGateway.GetMyCourseHierarchyAsync(userId, cancellationToken);
        return nodes.Where(node => node.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || node.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(node => new PlannerReferenceDto("school", node.Path, node.Name, connectionRef, null, null, null)).ToArray();
    }

    private async Task<IReadOnlyList<PlannerReferenceDto>> ResolveClassesAsync(string userId, string term, string? connectionRef, CancellationToken cancellationToken)
    {
        var courses = await coursesGateway.GetMyCoursesAsync(userId, 100, 1, cancellationToken);
        var candidates = new List<PlannerReferenceDto>();
        foreach (var course in courses.Items)
        {
            var groups = await participantsGateway.GetCourseGroupsAsync(userId, course.CourseId, cancellationToken);
            candidates.AddRange(groups.Where(group => group.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Select(group => new PlannerReferenceDto("class", group.GroupId, group.Name, connectionRef, "course", group.CourseId, course.FullName)));
        }
        return candidates.Take(50).ToArray();
    }

    private async Task<IReadOnlyList<PlannerReferenceDto>> ResolveStudentsAsync(string userId, string term, string? connectionRef, CancellationToken cancellationToken)
    {
        var courses = await coursesGateway.GetMyCoursesAsync(userId, 100, 1, cancellationToken);
        var candidates = new List<PlannerReferenceDto>();
        foreach (var course in courses.Items)
        {
            var participants = await participantsGateway.GetCourseParticipantsAsync(userId, course.CourseId, ParticipantStatusFilter.Active, 1, 50, true, false, null, cancellationToken);
            candidates.AddRange(participants.Participants.Where(student => student.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) || student.UserId.Equals(term, StringComparison.OrdinalIgnoreCase))
                .Select(student => new PlannerReferenceDto("student", student.UserId, student.FullName, connectionRef, "course", course.CourseId, course.FullName)));
        }
        return candidates.GroupBy(candidate => $"{candidate.ReferenceId}|{candidate.ParentReferenceId}", StringComparer.OrdinalIgnoreCase).Select(group => group.First()).Take(50).ToArray();
    }

    private static IReadOnlyList<ParsedPlannerTag> ParseTags(string text) => TagPattern.Matches(text).Select(match =>
    {
        var explicitToken = match.Groups["explicit"].Value.Trim();
        var bareToken = match.Groups["bare"].Value.Trim();
        var token = string.IsNullOrWhiteSpace(explicitToken) ? bareToken : explicitToken;
        var normalizedToken = token.ToLowerInvariant();
        var explicitType = normalizedToken switch { "escola" or "school" => "school", "curso" or "course" => "course", "turma" or "class" => "class", "aluno" or "student" => "student", _ => null };
        var term = explicitType is null ? token : match.Groups["value"].Value.Trim();
        return new ParsedPlannerTag($"/{token}", explicitType ?? "student", term);
    }).Where(tag => !string.IsNullOrWhiteSpace(tag.Term)).ToArray();

    private static CallToolResult Success<T>(T data, string message)
    {
        var response = new ToolResponse<T>("ok", data, [], Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, Message: message);
        return new() { Content = [new TextContentBlock { Text = message }], StructuredContent = JsonSerializer.SerializeToElement(response, JsonOptions), IsError = false };
    }

    private static CallToolResult Error(string message) => ToolResultHelper.Error<PlannerTagResolutionResponse>(message, errorCode: "portal_planner_tag_resolution_failed");

    private sealed record ParsedPlannerTag(string Token, string Type, string Term);
}

public sealed record PlannerTagResolutionResponse(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("tags")] IReadOnlyList<PlannerTagResolutionItem> Tags);

public sealed record PlannerTagResolutionItem(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("referenceType")] string ReferenceType,
    [property: JsonPropertyName("term")] string Term,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("candidates")] IReadOnlyList<PlannerReferenceDto> Candidates);
