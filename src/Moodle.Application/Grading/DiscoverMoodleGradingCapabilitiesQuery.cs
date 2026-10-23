using System.Text.Json.Serialization;
using MediatR;
using Moodle.Application.Abstractions;

namespace Moodle.Application.Grading;

public sealed record DiscoverMoodleGradingCapabilitiesQuery(
    string UserExternalId) : IRequest<MoodleGradingCapabilitiesReport>;

public sealed record MoodleWebServiceFunctionCatalog(
    [property: JsonPropertyName("serviceName")] string ServiceName,
    [property: JsonPropertyName("functions")] IReadOnlyCollection<string> Functions);

public sealed record MoodleWebServiceFunctionCapability(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("available")] bool Available);

public sealed record MoodleGradingCapabilitiesReport(
    [property: JsonPropertyName("serviceName")] string ServiceName,
    [property: JsonPropertyName("checkedAt")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("functions")] IReadOnlyList<MoodleWebServiceFunctionCapability> Functions,
    [property: JsonPropertyName("canReadSubmissions")] bool CanReadSubmissions,
    [property: JsonPropertyName("canReadGrades")] bool CanReadGrades,
    [property: JsonPropertyName("canReadFiles")] bool CanReadFiles,
    [property: JsonPropertyName("canReadRubrics")] bool CanReadRubrics,
    [property: JsonPropertyName("canWriteIndividualGrades")] bool CanWriteIndividualGrades,
    [property: JsonPropertyName("canWriteBatchGrades")] bool CanWriteBatchGrades,
    [property: JsonPropertyName("missingFunctions")] IReadOnlyList<string> MissingFunctions);

public sealed class DiscoverMoodleGradingCapabilitiesQueryHandler(
    IMoodleGradingCapabilitiesGateway gateway)
    : IRequestHandler<DiscoverMoodleGradingCapabilitiesQuery, MoodleGradingCapabilitiesReport>
{
    private static readonly MoodleFunctionRequirement[] Requirements =
    [
        new("mod_assign_get_assignments", "read_assignments_and_grading_definitions"),
        new("mod_assign_get_submissions", "read_submissions"),
        new("mod_assign_get_submission_status", "read_submission_status"),
        new("mod_assign_get_grades", "read_grades"),
        new("core_grading_get_definitions", "read_active_assignment_rubric"),
        new("mod_assign_save_grade", "write_individual_grade"),
        new("mod_assign_save_grades", "write_batch_grades"),
        new("core_files_get_files", "read_files")
    ];

    public async Task<MoodleGradingCapabilitiesReport> Handle(
        DiscoverMoodleGradingCapabilitiesQuery request,
        CancellationToken cancellationToken)
    {
        var catalog = await gateway.GetFunctionCatalogAsync(
            request.UserExternalId,
            cancellationToken);
        var available = catalog.Functions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var functions = Requirements
            .Select(requirement => new MoodleWebServiceFunctionCapability(
                requirement.Name,
                requirement.Purpose,
                available.Contains(requirement.Name)))
            .ToArray();

        return new MoodleGradingCapabilitiesReport(
            catalog.ServiceName,
            DateTimeOffset.UtcNow,
            functions,
            CanReadSubmissions: Has(functions, "mod_assign_get_submissions") &&
                Has(functions, "mod_assign_get_submission_status"),
            CanReadGrades: Has(functions, "mod_assign_get_grades"),
            CanReadFiles: Has(functions, "core_files_get_files"),
            CanReadRubrics: Has(functions, "core_grading_get_definitions"),
            CanWriteIndividualGrades: Has(functions, "mod_assign_save_grade"),
            CanWriteBatchGrades: Has(functions, "mod_assign_save_grades"),
            MissingFunctions: functions
                .Where(function => !function.Available)
                .Select(function => function.Name)
                .ToArray());
    }

    private static bool Has(
        IReadOnlyList<MoodleWebServiceFunctionCapability> functions,
        string name)
    {
        return functions.Any(function =>
            function.Available &&
            string.Equals(function.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record MoodleFunctionRequirement(string Name, string Purpose);
}
