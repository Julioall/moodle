using System.Text.Json.Serialization;
using MediatR;
using Moodle.Application.Abstractions;

namespace Moodle.Application.Grading;

public sealed record GradingTechnicalDiscoveryQuery(
    string UserExternalId) : IRequest<GradingTechnicalDiscoveryReport>;

public sealed record GradingTechnicalDiscoveryArea(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("confirmed")] bool Confirmed,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("evidence")] IReadOnlyList<string> Evidence,
    [property: JsonPropertyName("nextSteps")] IReadOnlyList<string> NextSteps);

public sealed record GradingWriteTokenDiscovery(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("connectorCanWrite")] bool ConnectorCanWrite,
    [property: JsonPropertyName("assignmentGradeWriteEnabled")] bool AssignmentGradeWriteEnabled,
    [property: JsonPropertyName("assignmentFeedbackWriteEnabled")] bool AssignmentFeedbackWriteEnabled,
    [property: JsonPropertyName("allowServiceTokenForReadOnlyQueries")] bool AllowServiceTokenForReadOnlyQueries);

public sealed record GradingTechnicalDiscoveryReport(
    [property: JsonPropertyName("serviceName")] string ServiceName,
    [property: JsonPropertyName("checkedAt")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("functions")] IReadOnlyList<MoodleWebServiceFunctionCapability> Functions,
    [property: JsonPropertyName("attachments")] GradingTechnicalDiscoveryArea Attachments,
    [property: JsonPropertyName("gradeWrite")] GradingTechnicalDiscoveryArea GradeWrite,
    [property: JsonPropertyName("permissions")] GradingTechnicalDiscoveryArea Permissions,
    [property: JsonPropertyName("rubricsAndScales")] GradingTechnicalDiscoveryArea RubricsAndScales,
    [property: JsonPropertyName("writeToken")] GradingWriteTokenDiscovery WriteToken,
    [property: JsonPropertyName("overallStatus")] string OverallStatus,
    [property: JsonPropertyName("blockingIssues")] IReadOnlyList<string> BlockingIssues,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

public sealed class GradingTechnicalDiscoveryQueryHandler(
    IMoodleGradingCapabilitiesGateway gateway,
    IMoodleCredentialsProvider credentialsProvider,
    IGradingTechnicalDiscoveryEnvironment environment)
    : IRequestHandler<GradingTechnicalDiscoveryQuery, GradingTechnicalDiscoveryReport>
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

    public async Task<GradingTechnicalDiscoveryReport> Handle(
        GradingTechnicalDiscoveryQuery request,
        CancellationToken cancellationToken)
    {
        var catalog = await gateway.GetFunctionCatalogAsync(request.UserExternalId, cancellationToken);
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var available = catalog.Functions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var functions = Requirements
            .Select(requirement => new MoodleWebServiceFunctionCapability(
                requirement.Name,
                requirement.Purpose,
                available.Contains(requirement.Name)))
            .ToArray();

        var blockers = new List<string>();
        var warnings = new List<string>();

        var attachments = BuildAttachments(functions, blockers);
        var gradeWrite = BuildGradeWrite(functions, credentials.CanWrite, blockers);
        var permissions = BuildPermissions(credentials.CanWrite, blockers);
        var rubricsAndScales = BuildRubricsAndScales(functions, blockers);
        var writeToken = new GradingWriteTokenDiscovery(
            "user_token",
            credentials.CanWrite,
            environment.AssignmentGradeWriteEnabled,
            environment.AssignmentFeedbackWriteEnabled,
            environment.AllowServiceTokenForReadOnlyQueries);

        if (!environment.AssignmentGradeWriteEnabled)
        {
            blockers.Add("Features:AssignmentGradeWriteEnabled esta desabilitado.");
        }

        if (!environment.AssignmentFeedbackWriteEnabled)
        {
            warnings.Add("Features:AssignmentFeedbackWriteEnabled esta desabilitado; feedback textual nao sera escrito.");
        }

        if (blockers.Count == 0)
        {
            warnings.Add("Permissoes reais, anexos, rubricas e escalas exigem prova com curso/tarefa Moodle reais.");
        }

        var overallStatus = blockers.Count > 0
            ? "blocked"
            : "requires_real_moodle_probe";

        return new GradingTechnicalDiscoveryReport(
            catalog.ServiceName,
            DateTimeOffset.UtcNow,
            functions,
            attachments,
            gradeWrite,
            permissions,
            rubricsAndScales,
            writeToken,
            overallStatus,
            blockers,
            warnings);
    }

    private static GradingTechnicalDiscoveryArea BuildAttachments(
        IReadOnlyList<MoodleWebServiceFunctionCapability> functions,
        List<string> blockers)
    {
        if (!Has(functions, "core_files_get_files"))
        {
            blockers.Add("core_files_get_files ausente; anexos nao podem ser lidos pela API configurada.");
            return new GradingTechnicalDiscoveryArea(
                "blocked",
                Confirmed: false,
                "A funcao core_files_get_files nao esta disponivel no servico Moodle atual.",
                [],
                ["Habilitar core_files_get_files ou confirmar alternativa para download de pluginfile."]);
        }

        return new GradingTechnicalDiscoveryArea(
            "requires_submission_file_probe",
            Confirmed: false,
            "core_files_get_files esta disponivel; falta provar download de pluginfile com uma entrega real.",
            ["core_files_get_files"],
                ["Selecionar uma entrega com anexo e validar download e leitura pelo MCP Resource."]);
    }

    private GradingTechnicalDiscoveryArea BuildGradeWrite(
        IReadOnlyList<MoodleWebServiceFunctionCapability> functions,
        bool connectorCanWrite,
        List<string> blockers)
    {
        var evidence = new List<string>();
        if (Has(functions, "mod_assign_save_grade"))
        {
            evidence.Add("mod_assign_save_grade");
        }
        else
        {
            blockers.Add("mod_assign_save_grade ausente; escrita individual de nota nao pode ser feita.");
        }

        evidence.Add($"conexao_can_write={connectorCanWrite.ToString().ToLowerInvariant()}");
        evidence.Add($"assignment_grade_write_enabled={environment.AssignmentGradeWriteEnabled.ToString().ToLowerInvariant()}");

        if (!connectorCanWrite)
        {
            blockers.Add("A conexao Moodle atual nao permite escrita.");
        }

        var blocked = !Has(functions, "mod_assign_save_grade") ||
            !connectorCanWrite ||
            !environment.AssignmentGradeWriteEnabled;

        return new GradingTechnicalDiscoveryArea(
            blocked ? "blocked" : "ready_for_sandbox_probe",
            Confirmed: false,
            blocked
                ? "A escrita por mod_assign_save_grade ainda esta bloqueada por funcao, permissao ou feature flag."
                : "mod_assign_save_grade esta disponivel, a conexao permite escrita e a feature flag de nota esta ativa.",
            evidence,
            ["Executar lancamento em uma tarefa sandbox antes de liberar producao."]);
    }

    private static GradingTechnicalDiscoveryArea BuildPermissions(
        bool connectorCanWrite,
        List<string> blockers)
    {
        if (!connectorCanWrite)
        {
            blockers.Add("Permissao de escrita da conexao esta desabilitada.");
            return new GradingTechnicalDiscoveryArea(
                "blocked",
                Confirmed: false,
                "A conexao atual foi cadastrada sem permissao de escrita.",
                ["conexao_can_write=false"],
                ["Cadastrar ou selecionar uma conexao Moodle com CanWrite=true."]);
        }

        return new GradingTechnicalDiscoveryArea(
            "unknown_requires_sandbox",
            Confirmed: false,
            "A role real de professor/tutor deve ser confirmada em uma tarefa sandbox.",
            ["conexao_can_write=true"],
            ["Validar leitura e escrita com o professor/tutor real no curso alvo."]);
    }

    private static GradingTechnicalDiscoveryArea BuildRubricsAndScales(
        IReadOnlyList<MoodleWebServiceFunctionCapability> functions,
        List<string> blockers)
    {
        var hasAssignments = Has(functions, "mod_assign_get_assignments");
        var hasGrades = Has(functions, "mod_assign_get_grades");
        var hasRubricDefinitions = Has(functions, "core_grading_get_definitions");
        if (!hasAssignments || !hasGrades || !hasRubricDefinitions)
        {
            if (!hasAssignments)
            {
                blockers.Add("mod_assign_get_assignments ausente; rubricas/escalas da tarefa nao podem ser inspecionadas.");
            }

            if (!hasGrades)
            {
                blockers.Add("mod_assign_get_grades ausente; notas/escalas existentes nao podem ser consultadas.");
            }

            if (!hasRubricDefinitions)
            {
                blockers.Add("core_grading_get_definitions ausente; associacao oficial de rubrica e conversao de pontos nao podem ser verificadas.");
            }

            return new GradingTechnicalDiscoveryArea(
                "blocked",
                Confirmed: false,
                "Faltam funcoes para inspecionar tarefa, rubrica oficial, escalas ou notas existentes.",
                [],
                ["Habilitar mod_assign_get_assignments, core_grading_get_definitions e mod_assign_get_grades no servico Moodle."]);
        }

        return new GradingTechnicalDiscoveryArea(
            "requires_assignment_probe",
            Confirmed: false,
            "Funcoes de tarefa, rubrica ativa e notas existem; a associacao e a escala ainda dependem de uma tarefa real configurada.",
            ["mod_assign_get_assignments", "core_grading_get_definitions", "mod_assign_get_grades"],
            ["Consultar uma tarefa com rubrica e escala configuradas."]);
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
