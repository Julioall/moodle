using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Tools;

[McpServerToolType]
public sealed class MoodleScormTools(
    IMoodleScormReader reader,
    ICurrentUserContext currentUser,
    IMoodleConnectionSelection moodleSelection,
    IMoodleAuditLogRepository auditLogs)
{
    [MoodleToolMetadata(
        Family = "scorm",
        Classification = "R3",
        Kind = "wrapper",
        CanonicalOperation = "mod_scorm_get_scorms_by_courses + pluginfile.php",
        ExposureStatus = "Keep",
        ExposureReason = "Leitura autenticada de pacote SCORM para inspeção de conteúdo, sem alteração no Moodle.",
        Evidence = "O pacote é baixado apenas da URL pluginfile.php emitida pelo Moodle ativo; token e bytes não entram no log ou no conteúdo estruturado de metadados.",
        RequiredPlatformPermission = "tool.classroom.view",
        RequiredOAuthScopes = "moodle.read.scorms moodle.read.contents",
        RequiredMoodleCapabilities = "mod_scorm_get_scorms_by_courses")]
    [McpServerTool(
        Name = "ler_scorm",
        Title = "Ler pacote SCORM",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ScormReadResult>))]
    [Description("Lê o pacote SCORM autenticado e informa contentExtractionStatus/contentCoverage. Manifesto, SCOs ou middleware não significam que o conteúdo pedagógico foi extraído. Pacotes grandes são disponibilizados como resource ZIP opaco, sem extração na resposta. Informe scormId quando houver mais de um pacote.")]
    public async Task<CallToolResult> LerScormAsync(
        [Description("Identificador do curso Moodle: courseId, shortName ou idnumber.")] string courseId,
        [Description("Identificador do SCORM (id da atividade). Opcional quando houver apenas um no curso.")] string? scormId = null,
        [Description("Alias da conexão Moodle a consultar.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        moodleSelection.Alias = moodleAlias;
        try
        {
            var data = await reader.ReadAsync(currentUser.Subject, courseId, scormId, cancellationToken);
            await AuditAsync(courseId, data, "success", null, startedAt, stopwatch.ElapsedMilliseconds, cancellationToken);
            var response = new ToolResponse<ScormReadResult>("ok", data, data.Warnings, null, DateTimeOffset.UtcNow);
            var content = new List<ContentBlock>
            {
                new TextContentBlock { Text = data.ContentExtractionStatus switch
                {
                    "resource_only" => $"SCORM '{data.Name}' disponibilizado como resource ZIP; o conteúdo não foi extraído nesta resposta.",
                    "middleware_only" => $"SCORM '{data.Name}': manifesto e estrutura lidos, mas o pacote contém somente middleware; conteúdo pedagógico não extraído.",
                    "structural_only" => $"SCORM '{data.Name}': somente estrutura identificada; nenhum conteúdo pedagógico foi extraído.",
                    "partial" => $"SCORM '{data.Name}' extraído parcialmente: {data.Scos.Count} SCO(s), {data.Files.Count} arquivo(s) textual(is). Confira os avisos e contentCoverage.",
                    _ => $"SCORM '{data.Name}' extraído: {data.Scos.Count} SCO(s), {data.Files.Count} arquivo(s) textual(is). Confira contentCoverage para o alcance da leitura."
                } }
            };
            if (data.PackageResource is not null)
            {
                content.Add(new ResourceLinkBlock
                {
                    Uri = data.PackageResource.Uri,
                    Name = data.PackageResource.Filename,
                    MimeType = data.PackageResource.MimeType,
                    Size = data.PackageResource.SizeBytes
                });
            }
            return new CallToolResult
            {
                Content = content,
                StructuredContent = JsonSerializer.SerializeToElement(response),
                IsError = false
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await AuditAsync(courseId, null, "timeout", MoodleErrorContract.RequestTimeout, startedAt, stopwatch.ElapsedMilliseconds, CancellationToken.None);
            return ToolResultHelper.Error<ScormReadResult>("A leitura do pacote SCORM excedeu o tempo limite.", errorCode: MoodleErrorContract.RequestTimeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            var descriptor = MoodleErrorContract.Describe(error);
            await AuditAsync(courseId, null, "error", descriptor.ErrorCode, startedAt, stopwatch.ElapsedMilliseconds, CancellationToken.None);
            return ToolResultHelper.Error<ScormReadResult>(error);
        }
    }

    private async Task AuditAsync(
        string courseId,
        ScormReadResult? data,
        string status,
        string? errorCode,
        DateTimeOffset startedAt,
        long durationMs,
        CancellationToken cancellationToken)
    {
        await auditLogs.AddAsync(new MoodleAuditLog
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
            ToolName = "ler_scorm",
            RiskLevel = ToolRiskLevel.SensitiveRead,
            ActorSubject = string.IsNullOrWhiteSpace(currentUser.Subject) ? "unknown" : currentUser.Subject,
            MoodleConnectionAlias = moodleSelection.Alias,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            DurationMs = durationMs,
            RequestSanitizedJson = JsonSerializer.Serialize(new { courseId, scormId = data?.ScormId }),
            ResponseSummaryJson = JsonSerializer.Serialize(new { data?.PackageSizeBytes, scoCount = data?.Scos.Count, fileCount = data?.Files.Count }),
            Status = status,
            ErrorCode = errorCode
        }, cancellationToken);
        await auditLogs.SaveChangesAsync(cancellationToken);
    }
}
