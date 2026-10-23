using System.Globalization;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Auditing;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Application.PendingActions;
using Moodle.Domain;

namespace Moodle.Infrastructure.MoodleApi;

internal sealed class MoodleUniversalWriteService(
    IMoodleFunctionCatalog catalog,
    IMoodleRestClient restClient,
    IMoodleCredentialsProvider credentialsProvider,
    IPendingActionService pendingActions,
    IActionConfirmationService confirmations,
    IPendingMoodleActionRepository pendingActionRepository,
    IMoodleAuditLogRepository auditLogs,
    IOptions<MoodleUniversalApiFeatureOptions> features,
    ICurrentUserContext currentUser,
    IMoodleAssignmentGradeReadGateway? gradeReadGateway = null,
    IOptions<PendingActionOptions>? pendingActionOptions = null) : IMoodleUniversalWriteService
{
    private readonly TimeSpan pendingActionExpiration =
        (pendingActionOptions?.Value ?? new PendingActionOptions())
            .ResolveExpiration(PendingActionTtlClass.Volatile);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SensitiveParameterFragments =
    [
        "password", "passwd", "pwd", "token", "secret", "authorization",
        "cookie", "connectionstring", "apikey", "privatekey", "accesskey", "refresh", "clientsecret", "jwt", "bearer"
    ];

    public async Task<MoodleWritePreview> PrepareAsync(
        string functionName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var parameterNames = parameters.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var connection = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        try
        {
            EnsureEnabled();
            var descriptor = await ResolveControlledWriteAsync(functionName, cancellationToken);
            EnsureWriteScope(descriptor.Name);
            EnsureNoSensitiveParameters(parameters);
            if (!connection.CanWrite)
            {
                throw new MoodleApiException("write_not_allowed", "A conexao Moodle selecionada nao permite escrita.");
            }
            var parameterHash = CreateParameterHash(parameters);
            var confirmationText = $"CONFIRMAR ESCRITA MOODLE {descriptor.Name.ToUpperInvariant()} {parameterHash[..12].ToUpperInvariant()}";
            var semanticPreview = await BuildSemanticPreviewAsync(
                descriptor.Name,
                connection,
                parameters,
                parameterNames,
                gradeReadGateway,
                cancellationToken);
            var preview = new
            {
                function = descriptor.Name,
                connectionAlias = connection.Alias,
                parameterNames,
                parameterHash,
                risk = descriptor.Risk.ToString(),
                semanticSummary = semanticPreview.Summary,
                changes = semanticPreview.Changes,
                affectedResources = semanticPreview.AffectedResources,
                estimatedAffectedRecords = semanticPreview.EstimatedAffectedRecords,
                warnings = semanticPreview.Warnings,
                execution = "Nenhuma chamada foi enviada ao Moodle; confirme explicitamente para executar uma unica vez."
            };
            var payload = new UniversalMoodleWritePayload(
                descriptor.Name,
                connection.ConnectionId,
                connection.Alias,
                parameters.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value, JsonOptions), StringComparer.Ordinal),
                parameterHash,
                parameterNames);
            var pending = await pendingActions.CreatePendingActionAsync(
                "moodle_prepare_write",
                ToolRiskLevel.CriticalHumanConfirmedWrite,
                payload,
                preview,
                confirmationText,
                pendingActionExpiration,
                TryGetCourseId(parameters),
                cancellationToken);
            var action = await pendingActionRepository.GetByIdAsync(pending.PendingActionId, cancellationToken)
                ?? throw new InvalidOperationException("A acao pendente universal nao foi encontrada apos a preparacao.");
            var preparedAt = DateTimeOffset.UtcNow;
            await auditLogs.AddAsync(new MoodleAuditLog
            {
                CorrelationId = action.CorrelationId,
                ToolName = "moodle_prepare_write",
                RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
                ActorSubject = action.CreatedBySubject,
                ActorEmail = action.CreatedByEmail,
                ActorMoodleUserId = action.CreatedByMoodleUserId,
                CourseId = action.CourseId,
                MoodleConnectionId = connection.ConnectionId,
                MoodleConnectionAlias = connection.Alias,
                MoodleFunction = descriptor.Name,
                PendingActionId = action.Id,
                StartedAt = preparedAt,
                FinishedAt = preparedAt,
                DurationMs = 0,
                RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
                {
                    connectionAlias = connection.Alias,
                    parameterNames,
                    parameterHash
                }),
                ResponseSummaryJson = JsonSerializer.Serialize(new { pendingActionId = action.Id, action.ExpiresAt }, JsonOptions),
                Status = "write_prepared"
            }, cancellationToken);
            await auditLogs.SaveChangesAsync(cancellationToken);

            return new MoodleWritePreview(
                pending.PendingActionId,
                descriptor.Name,
                parameterNames,
                parameterHash,
                confirmationText,
                pending.ExpiresAt,
                semanticPreview.Summary,
                semanticPreview.Changes,
                semanticPreview.AffectedResources,
                semanticPreview.EstimatedAffectedRecords,
                semanticPreview.Warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorCode = ex is MoodleApiException moodleError ? moodleError.ErrorCode : ex.GetType().Name;
            await RecordPreparationBlockedAsync(connection, functionName, parameterNames, startedAt, DateTimeOffset.UtcNow, errorCode, cancellationToken);
            await auditLogs.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    public async Task<MoodleWriteResult> ConfirmAsync(
        Guid pendingActionId,
        string confirmationText,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var action = await pendingActionRepository.GetByIdAsync(pendingActionId, cancellationToken)
            ?? throw new InvalidOperationException("Acao pendente nao encontrada.");
        if (!string.Equals(action.ToolName, "moodle_prepare_write", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A acao pendente nao pertence ao executor universal de escrita.");
        }

        var payload = JsonSerializer.Deserialize<UniversalMoodleWritePayload>(action.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Os dados da acao pendente estao invalidos.");
        var connection = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        if (!connection.CanWrite)
        {
            throw new MoodleApiException("write_not_allowed", "A conexao Moodle selecionada nao permite escrita.");
        }
        if (!string.Equals(connection.ConnectionId, payload.ConnectionId, StringComparison.Ordinal))
        {
            throw new MoodleApiException("wrong_moodle_alias", "A confirmacao deve usar a mesma conexao Moodle utilizada na previa.");
        }

        await ResolveControlledWriteAsync(payload.Function, cancellationToken);
        var requiredScope = MoodleWriteScopePolicy.ForFunction(payload.Function);
        var values = payload.Parameters.ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value.Clone(),
            StringComparer.Ordinal);
        if (!string.Equals(payload.ParameterHash, CreateParameterHash(values), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Os parametros da acao pendente foram alterados e a confirmacao foi invalidada.");
        }

        var confirmation = await confirmations.ConfirmAsync(
            pendingActionId,
            confirmationText,
            requiredScope,
            cancellationToken);

        if (confirmation.Status == "already_confirmed")
        {
            return new MoodleWriteResult("already_confirmed", action.Id, payload.Function, confirmation.AuditId, 0);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await restClient.CallWriteAsync(
                connection,
                payload.Function,
                values,
                cancellationToken);
            var responseSize = Encoding.UTF8.GetByteCount(response.GetRawText());
            await RecordExecutionAsync(action, payload, "write_executed", responseSize, startedAt, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, null, cancellationToken);
            await auditLogs.SaveChangesAsync(cancellationToken);
            return new MoodleWriteResult("executed", action.Id, payload.Function, confirmation.AuditId, responseSize);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorCode = ex is MoodleApiException moodleError ? moodleError.ErrorCode : ex.GetType().Name;
            var executionUnknown = MoodleWriteExecutionClassifier.IsUnknown(ex);
            if (executionUnknown)
            {
                action.MarkExecutionUnknown();
            }

            await pendingActionRepository.SaveChangesAsync(cancellationToken);
            await RecordExecutionAsync(
                action,
                payload,
                executionUnknown ? "write_execution_unknown" : "write_failed",
                0,
                startedAt,
                DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds,
                errorCode,
                cancellationToken);
            await auditLogs.SaveChangesAsync(cancellationToken);
            if (executionUnknown)
            {
                return new MoodleWriteResult(
                    "execution_unknown",
                    action.Id,
                    payload.Function,
                    confirmation.AuditId,
                    0);
            }
            throw;
        }
    }

    private async Task<MoodleFunctionDescriptor> ResolveControlledWriteAsync(string functionName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            throw new ArgumentException("A funcao Moodle e obrigatoria.", nameof(functionName));
        }

        var profile = await catalog.GetCurrentAsync(false, cancellationToken);
        var descriptor = profile.Functions.FirstOrDefault(function =>
            string.Equals(function.Name, functionName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (descriptor is null || !descriptor.IsAvailable)
        {
            throw new MoodleApiException("function_not_available", "A funcao solicitada nao esta habilitada para a conexao Moodle selecionada.");
        }
        if (descriptor.Risk is not (MoodleFunctionRisk.ControlledWrite or MoodleFunctionRisk.Destructive))
        {
            throw new MoodleApiException("function_not_write_allowed", "A funcao solicitada nao requer o fluxo de escrita confirmado.");
        }

        return descriptor;
    }

    private void EnsureWriteScope(string functionName)
    {
        var requiredScope = MoodleWriteScopePolicy.ForFunction(functionName);
        if (!currentUser.HasScope(requiredScope))
        {
            throw new MoodleApiException(
                "moodle_write_scope_required",
                $"O escopo '{requiredScope}' e obrigatorio para esta familia de escrita.");
        }
    }

    private Task RecordExecutionAsync(
        PendingMoodleAction action,
        UniversalMoodleWritePayload payload,
        string status,
        int responseSize,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        long durationMs,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        return auditLogs.AddAsync(new MoodleAuditLog
        {
            CorrelationId = action.CorrelationId,
            ToolName = "moodle_confirm_write",
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            ActorSubject = action.CreatedBySubject,
            ActorEmail = action.CreatedByEmail,
            ActorMoodleUserId = action.CreatedByMoodleUserId,
            CourseId = action.CourseId,
            MoodleConnectionId = payload.ConnectionId,
            MoodleConnectionAlias = payload.ConnectionAlias,
            MoodleFunction = payload.Function,
            PendingActionId = action.Id,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            DurationMs = durationMs,
            RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                payload.ConnectionAlias,
                payload.ParameterNames,
                payload.ParameterHash
            }),
            ResponseSummaryJson = JsonSerializer.Serialize(new { responseSize, durationMs }, JsonOptions),
            Status = status,
            ErrorCode = errorCode,
            // A mensagem remota pode refletir valores de parâmetros; o código normalizado é suficiente para auditoria.
            ErrorMessage = null
        }, cancellationToken);
    }

    private Task RecordPreparationBlockedAsync(
        MoodleCredentials connection,
        string functionName,
        IReadOnlyList<string> parameterNames,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        string errorCode,
        CancellationToken cancellationToken)
    {
        return auditLogs.AddAsync(new MoodleAuditLog
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
            ToolName = "moodle_prepare_write",
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            ActorSubject = string.IsNullOrWhiteSpace(currentUser.Subject) ? "unknown" : currentUser.Subject,
            ActorEmail = currentUser.Email,
            MoodleConnectionId = connection.ConnectionId,
            MoodleConnectionAlias = connection.Alias,
            MoodleFunction = string.IsNullOrWhiteSpace(functionName) ? null : functionName.Trim(),
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            DurationMs = Math.Max(0, (long)(finishedAt - startedAt).TotalMilliseconds),
            RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                connectionAlias = connection.Alias,
                parameterNames
            }),
            ResponseSummaryJson = "{}",
            Status = "write_prepare_blocked",
            ErrorCode = errorCode
        }, cancellationToken);
    }

    private void EnsureEnabled()
    {
        if (!features.Value.UniversalMoodleWriteEnabled)
        {
            throw new InvalidOperationException("A escrita universal Moodle esta desabilitada. Habilite Features:UniversalMoodleWriteEnabled somente apos revisao administrativa.");
        }
    }

    private static async Task<SemanticWritePreview> BuildSemanticPreviewAsync(
        string functionName,
        MoodleCredentials connection,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> parameterNames,
        IMoodleAssignmentGradeReadGateway? gradeReadGateway,
        CancellationToken cancellationToken)
    {
        var normalized = functionName.Trim().ToLowerInvariant();
        var warnings = new List<string>();
        var summary = normalized switch
        {
            "mod_assign_save_grade" => BuildGradeSummary(parameters, warnings),
            "mod_assign_save_grades" => BuildBatchGradeSummary(parameters, warnings),
            "core_message_send_instant_messages" or "core_message_send_messages_to_conversation" => BuildMessageSummary(parameters, warnings),
            "core_calendar_create_calendar_events" => BuildCalendarSummary(parameters, warnings),
            _ => $"Executar a escrita Moodle '{functionName.Trim()}'."
        };

        var affectedResources = normalized switch
        {
            "mod_assign_save_grade" => BuildGradeResources(parameters),
            "mod_assign_save_grades" => BuildBatchGradeResources(parameters),
            "core_message_send_instant_messages" or "core_message_send_messages_to_conversation" => BuildMessageResources(parameters),
            "core_calendar_create_calendar_events" => BuildCalendarResources(parameters),
            _ => Array.Empty<string>()
        };

        var estimatedRecords = TryEstimateAffectedRecords(normalized, parameters);
        if (estimatedRecords is null)
        {
            warnings.Add("A quantidade de registros afetados não foi determinada.");
        }
        if (string.IsNullOrWhiteSpace(connection.Alias))
        {
            warnings.Add("A conexão Moodle não possui alias de exibição.");
        }

        var previousValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (normalized == "mod_assign_save_grade" &&
            gradeReadGateway is not null &&
            TryGetParameter(parameters, ["assignmentid", "assignmentId"], out var assignmentId) &&
            TryGetParameter(parameters, ["studentid", "studentId", "userid", "userId"], out var studentId))
        {
            try
            {
                var existing = await gradeReadGateway.GetExistingGradeAsync(
                    connection.Username,
                    assignmentId,
                    studentId,
                    cancellationToken);
                if (existing is not null)
                {
                    previousValues["grade"] = existing.HasGrade && existing.Grade is not null
                        ? existing.Grade.Value.ToString(CultureInfo.InvariantCulture)
                        : null;
                    previousValues["gradevalue"] = previousValues["grade"];
                    previousValues["feedback"] = existing.Feedback;
                }
                else
                {
                    warnings.Add("A nota anterior não foi encontrada; confirme o estudante e a atividade antes de executar.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"Não foi possível consultar o valor anterior da nota ({exception.GetType().Name}); a prévia mostra somente o novo valor.");
            }
        }
        else if (normalized == "mod_assign_save_grade")
        {
            warnings.Add("A prévia não conseguiu consultar a nota anterior porque assignmentid/studentid não foram resolvidos.");
        }
        else if (normalized is "mod_assign_save_grades" or "core_message_send_instant_messages" or "core_message_send_messages_to_conversation")
        {
            warnings.Add("Valores anteriores não se aplicam ou não foram consultados para esta operação em lote/comunicação.");
        }

        var changes = parameterNames
            .Select(name => new MoodleWritePreviewChange(
                name,
                previousValues.TryGetValue(name, out var previous) ? previous : null,
                NewValue: FormatPreviewValue(parameters[name])))
            .ToArray();

        return new SemanticWritePreview(summary, changes, affectedResources, estimatedRecords, warnings);
    }

    private static string BuildGradeSummary(IReadOnlyDictionary<string, object?> parameters, List<string> warnings)
    {
        var assignment = TryGetParameter(parameters, ["assignmentid", "assignmentId"], out var assignmentId) ? assignmentId : "atividade não identificada";
        var student = TryGetParameter(parameters, ["studentid", "studentId", "userid", "userId"], out var studentId) ? studentId : "estudante não identificado";
        if (!TryGetParameter(parameters, ["grade", "gradevalue"], out _))
        {
            warnings.Add("A nova nota não foi identificada nos parâmetros da função.");
        }
        return $"Atualizar a nota do estudante {student} na atividade {assignment}.";
    }

    private static string BuildBatchGradeSummary(IReadOnlyDictionary<string, object?> parameters, List<string> warnings)
    {
        var count = TryGetArrayCount(parameters, ["grades", "items", "gradeitems"]);
        if (count is null)
        {
            warnings.Add("Informe uma lista explícita de notas para que o impacto do lote seja calculado.");
            return "Atualizar notas em lote; a quantidade exata não foi identificada.";
        }
        return $"Atualizar {count.Value} notas de atividades Moodle conforme a lista informada.";
    }

    private static string BuildMessageSummary(IReadOnlyDictionary<string, object?> parameters, List<string> warnings)
    {
        var count = TryGetArrayCount(parameters, ["messages"]);
        if (count is null)
        {
            warnings.Add("A lista de destinatários/mensagens não foi identificada; o impacto pode ser maior que o previsto.");
            return "Enviar mensagens Moodle aos destinatários informados.";
        }
        return $"Enviar {count.Value} mensagem(ns) Moodle aos destinatários informados.";
    }

    private static string BuildCalendarSummary(IReadOnlyDictionary<string, object?> parameters, List<string> warnings)
    {
        var count = TryGetArrayCount(parameters, ["events"]);
        return count is null
            ? "Criar evento(s) no calendário Moodle conforme os parâmetros informados."
            : $"Criar {count.Value} evento(s) no calendário Moodle.";
    }

    private static IReadOnlyList<string> BuildGradeResources(IReadOnlyDictionary<string, object?> parameters)
    {
        var resources = new List<string> { "assignment", "submission", "grade" };
        if (TryGetParameter(parameters, ["assignmentid", "assignmentId"], out var assignmentId))
            resources.Add($"assignment:{assignmentId}");
        if (TryGetParameter(parameters, ["studentid", "studentId", "userid", "userId"], out var studentId))
            resources.Add($"student:{studentId}");
        return resources;
    }

    private static IReadOnlyList<string> BuildBatchGradeResources(IReadOnlyDictionary<string, object?> parameters)
    {
        var resources = new List<string> { "assignment", "submission", "grade", "bulk-selection" };
        AppendArrayResourceIds(resources, parameters, ["grades", "items", "gradeitems"], ["assignmentid", "assignmentId"], "assignment");
        AppendArrayResourceIds(resources, parameters, ["grades", "items", "gradeitems"], ["studentid", "studentId", "userid", "userId"], "student");
        return resources;
    }

    private static IReadOnlyList<string> BuildMessageResources(IReadOnlyDictionary<string, object?> parameters)
    {
        var resources = new List<string> { "message", "recipient" };
        AppendArrayResourceIds(resources, parameters, ["messages"], ["touserid", "toUserId", "userid", "userId"], "recipient");
        return resources;
    }

    private static IReadOnlyList<string> BuildCalendarResources(IReadOnlyDictionary<string, object?> parameters)
    {
        var resources = new List<string> { "calendar_event", "course" };
        AppendArrayResourceIds(resources, parameters, ["events"], ["courseid", "courseId"], "course");
        return resources;
    }

    private static void AppendArrayResourceIds(
        ICollection<string> resources,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> arrayNames,
        IReadOnlyList<string> idNames,
        string prefix)
    {
        if (!TryGetParameterObject(parameters, arrayNames, out var raw) || raw is not JsonElement element ||
            element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in element.EnumerateArray().Take(20))
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var idName in idNames)
            {
                if (item.TryGetProperty(idName, out var id) && id.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                {
                    var value = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        resources.Add($"{prefix}:{value}");
                        break;
                    }
                }
            }
        }
    }

    private static int? TryEstimateAffectedRecords(
        string functionName,
        IReadOnlyDictionary<string, object?> parameters)
    {
        if ((functionName == "core_message_send_instant_messages" || functionName == "core_message_send_messages_to_conversation") &&
            TryGetParameterObject(parameters, ["messages"], out var messages))
        {
            if (messages is JsonElement element && element.ValueKind == JsonValueKind.Array)
            {
                return element.GetArrayLength();
            }

            if (messages is System.Collections.ICollection collection)
            {
                return collection.Count;
            }
        }

        return functionName == "mod_assign_save_grade"
            ? 1
            : functionName == "mod_assign_save_grades"
            ? TryGetArrayCount(parameters, ["grades", "items", "gradeitems"])
            : functionName == "core_calendar_create_calendar_events"
                ? TryGetArrayCount(parameters, ["events"])
                : null;
    }

    private static bool TryGetParameter(
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> names,
        out string value)
    {
        if (TryGetParameterObject(parameters, names, out var raw) && raw is not null)
        {
            value = raw is JsonElement element && element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : raw is JsonElement json ? json.GetRawText() : Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetParameterObject(
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> names,
        out object? value)
    {
        foreach (var name in names)
        {
            var pair = parameters.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                value = pair.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static int? TryGetArrayCount(IReadOnlyDictionary<string, object?> parameters, IReadOnlyList<string> names)
    {
        if (!TryGetParameterObject(parameters, names, out var value) || value is null)
            return null;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
            return element.GetArrayLength();
        return value is System.Collections.ICollection collection ? collection.Count : null;
    }

    private static string? FormatPreviewValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = value switch
        {
            JsonElement element => FormatJsonElement(element),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };

        const int maxLength = 160;
        return text.Length <= maxLength ? text : $"{text[..maxLength]}...";
    }

    private static string FormatJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Array => $"[{element.GetArrayLength()} itens]",
        JsonValueKind.Object => "{objeto}",
        _ => element.GetRawText()
    };

    private sealed record SemanticWritePreview(
        string Summary,
        IReadOnlyList<MoodleWritePreviewChange> Changes,
        IReadOnlyList<string> AffectedResources,
        int? EstimatedAffectedRecords,
        IReadOnlyList<string> Warnings);

    private static void EnsureNoSensitiveParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        var sensitive = MoodleParameterSerializer.Flatten(parameters).Keys.FirstOrDefault(IsSensitiveParameterName);
        if (sensitive is not null)
        {
            throw new MoodleApiException("sensitive_write_parameter_blocked", "A escrita universal não aceita parâmetros que possam conter credenciais ou segredos técnicos.");
        }
    }

    private static bool IsSensitiveParameterName(string name)
    {
        var components = name.Split(['[', ']'], StringSplitOptions.RemoveEmptyEntries);
        return components.Any(component =>
        {
            var normalized = component.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
            return SensitiveParameterFragments.Any(fragment => normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static string CreateParameterHash(IReadOnlyDictionary<string, object?> parameters)
    {
        var json = JsonSerializer.Serialize(parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal), JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static long? TryGetCourseId(IReadOnlyDictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("courseid", out var courseId) || courseId is null)
        {
            return null;
        }

        return long.TryParse(Convert.ToString(courseId, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private sealed record UniversalMoodleWritePayload(
        string Function,
        string ConnectionId,
        string ConnectionAlias,
        IReadOnlyDictionary<string, JsonElement> Parameters,
        string ParameterHash,
        IReadOnlyList<string> ParameterNames);
}
