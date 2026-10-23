using System.Globalization;
using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.Auditing;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Infrastructure.MoodleApi;

internal sealed class MoodleWriteReconciliationService(
    IPendingMoodleActionRepository pendingActions,
    ICurrentUserContext currentUser,
    IGradingReviewRepository? gradingRepository = null,
    IMoodleAssignmentGradeReadGateway? assignmentGradeReadGateway = null,
    IMoodleGradebookGateway? gradebookGateway = null,
    IMoodleAssignmentSubmissionStatusGateway? submissionStatusGateway = null,
    IConnectorExecutionContext? executionContext = null,
    IMoodleConnectionSelection? connectionSelection = null,
    IMoodleBackgroundConnectionContextRestorer? backgroundConnectionContextRestorer = null) : IMoodleWriteReconciliationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<MoodleWriteReconciliationResult> ReconcileAsync(
        Guid pendingActionId,
        string resolution,
        CancellationToken cancellationToken) =>
        ReconcileAsync(pendingActionId, resolution, null, cancellationToken);

    public async Task<MoodleWriteReconciliationResult> ReconcileAsync(
        Guid pendingActionId,
        string resolution,
        Guid? gradingItemId,
        CancellationToken cancellationToken)
    {
        var action = await pendingActions.GetByIdAsync(pendingActionId, cancellationToken)
            ?? throw new MoodleApiException(MoodleErrorContract.PendingActionNotFound, "Ação pendente não encontrada.");

        if (!string.Equals(action.CreatedBySubject, currentUser.Subject, StringComparison.Ordinal) &&
            !currentUser.HasPlatformPermission("tool.pending_actions.manage"))
        {
            throw new InvalidOperationException("Apenas o criador da ação ou um administrador pode reconciliá-la.");
        }

        JsonDocument payloadDocument;
        try
        {
            payloadDocument = JsonDocument.Parse(action.PayloadJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("O payload da ação não pode ser lido para reconciliação.");
        }

        using (payloadDocument)
        {
            if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("O payload da ação não tem o formato esperado para reconciliação.");
            }

            var isGradingPublication = action.ToolName is "criar_previa_lancamento_lote" or "confirmar_lancamento_lote_moodle";
            if (isGradingPublication)
            {
                return await ReconcileGradingItemAsync(
                    action,
                    payloadDocument.RootElement,
                    resolution,
                    gradingItemId,
                    cancellationToken);
            }

            if (gradingItemId is not null)
            {
                throw new ArgumentException("gradingItemId só pode ser usado para reconciliar publicações de correção.", nameof(gradingItemId));
            }

            if (action.Status != PendingActionStatus.ExecutionUnknown)
            {
                throw new MoodleApiException(
                    MoodleErrorContract.InvalidPendingAction,
                    $"A ação só pode ser reconciliada no estado ExecutionUnknown; estado atual: {action.Status}.");
            }

            var functionName = payloadDocument.RootElement.TryGetProperty("function", out var function)
                ? function.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(functionName))
            {
                throw new InvalidOperationException("A ação não contém a função Moodle necessária para reconciliação.");
            }

            var scope = MoodleWriteScopePolicy.ForFunction(functionName);
            if (!currentUser.HasScope(scope))
            {
                throw new InvalidOperationException($"Escopo obrigatório ausente: {scope}.");
            }

            var normalizedResolution = resolution.Trim().ToLowerInvariant() switch
            {
                "executed" or "applied" or "aplicada" => (PendingActionStatus.Executed, "executed"),
                "not_applied" or "not-applied" or "failed" or "nao_aplicada" or "não_aplicada" => (PendingActionStatus.Failed, "not_applied"),
                _ => throw new ArgumentException("Para esta escrita, a resolução deve ser 'executed' ou 'not_applied'.", nameof(resolution))
            };

            var now = DateTimeOffset.UtcNow;
            var audit = new MoodleAuditLog
            {
                CorrelationId = action.CorrelationId,
                ToolName = "moodle_reconcile_write",
                RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
                ActorSubject = currentUser.Subject,
                ActorEmail = currentUser.Email,
                ActorMoodleUserId = action.CreatedByMoodleUserId,
                CourseId = action.CourseId,
                MoodleFunction = functionName,
                PendingActionId = action.Id,
                StartedAt = now,
                FinishedAt = now,
                DurationMs = 0,
                RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
                {
                    actionId = action.Id,
                    function = functionName,
                    resolution = normalizedResolution.Item2
                }),
                ResponseSummaryJson = JsonSerializer.Serialize(new { resolution = normalizedResolution.Item2 }, JsonOptions),
                Status = normalizedResolution.Item2 == "executed"
                    ? "write_reconciled_executed"
                    : "write_reconciled_not_applied"
            };

            var claim = await pendingActions.TryResolveExecutionUnknownWithAuditAsync(
                action.Id,
                normalizedResolution.Item1,
                audit,
                cancellationToken);
            if (!claim.ResolvedByCaller)
            {
                if (claim.Status == PendingActionStatus.ExecutionUnknown)
                {
                    throw new InvalidOperationException("A reconciliação concorrente não pôde ser concluída; tente novamente.");
                }

                return new MoodleWriteReconciliationResult(
                    "already_reconciled",
                    action.Id,
                    functionName,
                    claim.Status == PendingActionStatus.Executed ? "executed" : "not_applied",
                    claim.AuditId,
                    "A ação já havia sido reconciliada e não foi alterada novamente.");
            }

            return new MoodleWriteReconciliationResult(
                "reconciled",
                action.Id,
                functionName,
                normalizedResolution.Item2,
                claim.AuditId,
                normalizedResolution.Item1 == PendingActionStatus.Executed
                    ? "A operação foi marcada como aplicada no Moodle; nenhuma nova requisição foi enviada."
                    : "A operação foi marcada como não aplicada; nenhuma nova requisição foi enviada. Crie uma nova prévia se necessário.");
        }
    }

    private async Task<MoodleWriteReconciliationResult> ReconcileGradingItemAsync(
        PendingMoodleAction action,
        JsonElement payloadRoot,
        string resolution,
        Guid? requestedGradingItemId,
        CancellationToken cancellationToken)
    {
        if (requestedGradingItemId is not Guid gradingItemId || gradingItemId == Guid.Empty)
        {
            throw new ArgumentException("Para reconciliar uma correção, informe gradingItemId. Cada item é resolvido separadamente.", nameof(requestedGradingItemId));
        }
        if (!string.Equals(resolution?.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Para correções, resolution deve ser 'auto'; decisões manuais globais não são aceitas.", nameof(resolution));
        }
        if (action.Status is not (PendingActionStatus.ExecutionUnknown or PendingActionStatus.Executed or PendingActionStatus.Failed or PendingActionStatus.PartiallyCompleted))
        {
            throw new MoodleApiException(
                MoodleErrorContract.InvalidPendingAction,
                $"A ação ainda não pode ser reconciliada; estado atual: {action.Status}.");
        }
        if (action.ExecutionLeaseUntil is { } activeLease && activeLease > DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("A publicação ainda possui um worker ativo; aguarde o fim do lease antes de reconciliar.");
        }
        if (gradingRepository is null)
        {
            throw new InvalidOperationException("O ledger de correção está indisponível; nenhuma reconciliação foi realizada.");
        }
        if (!currentUser.HasScope(MoodleWriteScopePolicy.ForFunction("mod_assign_save_grade")))
        {
            throw new InvalidOperationException("Escopo obrigatório ausente: moodle.write.assignments.grade.");
        }

        var payload = DeserializeGradingPayload(payloadRoot);
        if (payload.PublicationId is not Guid publicationId || publicationId == Guid.Empty)
        {
            throw new InvalidOperationException("A publicação não possui identificador de ledger para reconciliação por item.");
        }
        var payloadMatches = payload.Items.Where(item => item.GradingItemId == gradingItemId).ToArray();
        if (payloadMatches.Length != 1)
        {
            throw new InvalidOperationException("O item informado não aparece uma única vez na seleção aprovada desta ação.");
        }
        var payloadItem = payloadMatches[0];
        var item = await gradingRepository.GetItemAsync(gradingItemId, cancellationToken)
            ?? throw new InvalidOperationException("O item de correção não está mais disponível no ledger.");
        if (!long.TryParse(payloadItem.CourseId, NumberStyles.None, CultureInfo.InvariantCulture, out var courseId) ||
            !long.TryParse(payloadItem.AssignmentId, NumberStyles.None, CultureInfo.InvariantCulture, out var assignmentId) ||
            !long.TryParse(payloadItem.StudentId, NumberStyles.None, CultureInfo.InvariantCulture, out var studentId) ||
            item.CourseId != courseId ||
            item.AssignmentId != assignmentId ||
            item.MoodleUserId != studentId ||
            item.AttemptNumber != payloadItem.AttemptNumber)
        {
            throw new InvalidOperationException("A identidade do item local diverge do payload aprovado; nenhuma leitura ou escrita foi feita.");
        }

        var publicationClaim = await gradingRepository.GetPublicationClaimAsync(
            publicationId,
            action.Id,
            gradingItemId,
            cancellationToken);
        if (publicationClaim is null)
        {
            throw new InvalidOperationException("Não existe claim desta ação e deste item para reconciliar.");
        }
        if (item.CommitStatus == GradingCommitStatus.Succeeded && publicationClaim.Status == "Succeeded")
        {
            await TryFinalizeReconciledActionAsync(action, publicationId, cancellationToken);
            return new MoodleWriteReconciliationResult(
                "already_reconciled",
                action.Id,
                "mod_assign_save_grade",
                "applied",
                null,
                "Este item já havia sido confirmado como publicado; nenhuma nova leitura ou escrita foi feita.",
                gradingItemId,
                "applied",
                "previous_item_checkpoint");
        }
        if (item.CommitStatus != GradingCommitStatus.ExecutionUnknown || publicationClaim.Status != "ExecutionUnknown")
        {
            throw new InvalidOperationException("A reconciliação exige que o item e o claim estejam ambos em ExecutionUnknown.");
        }

        var batch = await GradingMoodleConnectionContext.RestorePublicationAsync(
            gradingRepository,
            currentUser,
            payload.BatchJobId,
            executionContext,
            connectionSelection,
            backgroundConnectionContextRestorer,
            cancellationToken,
            gradingItemId);
        var expectedConnectionKey = ResolvePublicationConnectionKey(batch);
        if (string.IsNullOrWhiteSpace(payload.ConnectionKey) ||
            !string.Equals(payload.ConnectionKey, expectedConnectionKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A conexão capturada não corresponde à conexão aprovada; nenhuma consulta Moodle foi realizada.");
        }

        if (assignmentGradeReadGateway is null || gradebookGateway is null)
        {
            throw new InvalidOperationException("As leituras necessárias de nota e feedback estão indisponíveis; nenhuma reconciliação foi realizada.");
        }

        var localDraftMatches = item.FinalGrade == payloadItem.Grade &&
            string.Equals(item.FinalFeedback ?? string.Empty, payloadItem.FeedbackText ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(item.SubmissionContentHash ?? string.Empty, payloadItem.SubmissionContentHash ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(item.ContextHash ?? string.Empty, payloadItem.ContextHash ?? string.Empty, StringComparison.Ordinal);

        var outcome = "inconclusive";
        var evidence = "remote_read_incomplete";
        string? eventErrorCode = null;
        AssignmentExistingGrade? observedGrade = null;
        DateTimeOffset? remoteObservedAt = null;
        var userExternalId = batch.CreatedByMoodleUserId?.ToString(CultureInfo.InvariantCulture) ?? batch.CreatedBySubject;

        try
        {
            if (payloadItem.AttemptNumber is int expectedAttempt)
            {
                if (submissionStatusGateway is null)
                {
                    evidence = "attempt_status_unavailable";
                    eventErrorCode = "reconciliation_attempt_status_unavailable";
                }
                else
                {
                    var attemptStatus = await submissionStatusGateway.GetSubmissionStatusLiveAsync(
                        userExternalId,
                        payloadItem.AssignmentId,
                        payloadItem.StudentId,
                        cancellationToken);
                    if (attemptStatus?.AttemptNumber is null)
                    {
                        evidence = "attempt_status_unobserved";
                        eventErrorCode = "reconciliation_attempt_status_unobserved";
                    }
                    else if (attemptStatus.AttemptNumber != expectedAttempt)
                    {
                        outcome = "conflict";
                        evidence = "current_attempt_differs_from_approved_attempt";
                    }
                    else if (!string.IsNullOrWhiteSpace(attemptStatus.SubmissionStatus) &&
                             !string.Equals(attemptStatus.SubmissionStatus, "submitted", StringComparison.OrdinalIgnoreCase))
                    {
                        outcome = "conflict";
                        evidence = "current_submission_is_not_submitted";
                    }
                }
            }

            if (outcome != "conflict" && evidence is not ("attempt_status_unavailable" or "attempt_status_unobserved"))
            {
                var assignmentGrade = await assignmentGradeReadGateway.GetExistingGradeAsync(
                    userExternalId,
                    payloadItem.AssignmentId,
                    payloadItem.StudentId,
                    cancellationToken);
                var gradebook = await gradebookGateway.GetStudentGradebookAsync(
                    payloadItem.CourseId,
                    payloadItem.StudentId,
                    GradebookReadOptions.LiveWithoutScale,
                    cancellationToken);
                remoteObservedAt = gradebook.ReadAt ?? DateTimeOffset.UtcNow;
                observedGrade = GradingPublicationGradebookProjection.Combine(
                    payloadItem.AssignmentId,
                    payloadItem.StudentId,
                    assignmentGrade,
                    gradebook);

                if (observedGrade is null)
                {
                    outcome = "inconclusive";
                    evidence = "remote_grade_or_feedback_not_fully_observed";
                    eventErrorCode = "reconciliation_readback_incomplete";
                }
                else if (GradingPublicationGradebookProjection.MatchesApprovedValues(payloadItem, observedGrade))
                {
                    if (localDraftMatches)
                    {
                        outcome = "applied";
                        evidence = payloadItem.AttemptNumber is null
                            ? "approved_grade_and_feedback_observed; workflow_release_unverified"
                            : "approved_attempt_grade_and_feedback_observed; workflow_release_unverified";
                    }
                    else
                    {
                        outcome = "conflict";
                        evidence = "remote_target_observed_but_local_draft_changed_after_authorization";
                    }
                }
                else if (!string.IsNullOrWhiteSpace(payloadItem.PreflightHash) &&
                         (string.Equals(
                              GradingPublicationGradebookProjection.ComputePreflightHash(
                                  payload.ConnectionKey,
                                  payloadItem,
                                  observedGrade),
                              payloadItem.PreflightHash,
                              StringComparison.Ordinal) ||
                          string.Equals(
                              GradingPublicationGradebookProjection.ComputeLegacyPreflightHash(
                                  payload.ConnectionKey,
                                  payloadItem,
                                  observedGrade with { Grade = assignmentGrade?.Grade ?? observedGrade.Grade }),
                              payloadItem.PreflightHash,
                              StringComparison.Ordinal)))
                {
                    // The approved baseline is visible, but an old request
                    // might still be in flight. A matching baseline alone is
                    // not positive proof that the request was never applied.
                    outcome = "inconclusive";
                    evidence = "remote_state_matches_baseline_but_request_completion_is_unknown";
                }
                else
                {
                    outcome = "conflict";
                    evidence = "remote_state_matches_neither_approved_target_nor_baseline";
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            outcome = "inconclusive";
            evidence = "remote_read_failed_or_incomplete";
            eventErrorCode = "reconciliation_read_failed";
        }

        if (outcome == "applied")
        {
            item.ResolveCommitExecutionUnknown(applied: true);
        }

        var now = DateTimeOffset.UtcNow;
        var eventRecord = new GradingPublicationAttemptRecord(
            action.Id,
            publicationId,
            gradingItemId,
            payloadItem.AttemptNumber,
            DispatchAttemptNumber: 1,
            EventType: outcome switch
            {
                "applied" => "item_reconciled_target_observed",
                "conflict" => "item_reconciliation_conflict",
                _ => "item_reconciliation_inconclusive"
            },
            ItemState: outcome == "applied" ? "Succeeded" : "ExecutionUnknown",
            WorkerId: publicationClaim.WorkerId,
            FencingToken: publicationClaim.FencingToken,
            ErrorCode: eventErrorCode,
            EvidenceKind: outcome switch
            {
                "applied" => "remote_readback",
                "conflict" => "remote_conflict",
                _ => "remote_readback_inconclusive"
            },
            RemoteObservedAtUtc: remoteObservedAt,
            CreatedAtUtc: now);
        var audit = new MoodleAuditLog
        {
            CorrelationId = action.CorrelationId,
            BatchJobId = payload.BatchJobId,
            ToolName = "moodle_reconcile_write",
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            ActorSubject = currentUser.Subject,
            ActorEmail = currentUser.Email,
            ActorMoodleUserId = action.CreatedByMoodleUserId,
            CourseId = courseId,
            MoodleConnectionId = batch.MoodleConnectionId,
            MoodleConnectionAlias = batch.ConnectionAlias,
            MoodleFunction = "mod_assign_save_grade",
            PendingActionId = action.Id,
            StartedAt = now,
            FinishedAt = now,
            DurationMs = 0,
            RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                actionId = action.Id,
                publicationId,
                gradingItemId,
                resolution = "auto"
            }),
            ResponseSummaryJson = JsonSerializer.Serialize(new
            {
                gradingItemId,
                outcome,
                evidence,
                remoteObserved = observedGrade is not null
            }, JsonOptions),
            Status = outcome switch
            {
                "applied" => "grading_item_reconciled_applied",
                "conflict" => "grading_item_reconciliation_conflict",
                _ => "grading_item_reconciliation_inconclusive"
            }
        };

        await gradingRepository.SavePublicationReconciliationCheckpointAsync(
            outcome == "applied" ? "Succeeded" : "ExecutionUnknown",
            eventRecord,
            audit,
            cancellationToken);

        if (outcome == "applied")
        {
            await TryFinalizeReconciledActionAsync(action, publicationId, cancellationToken);
        }

        return new MoodleWriteReconciliationResult(
            outcome == "applied" ? "reconciled" : outcome,
            action.Id,
            "mod_assign_save_grade",
            "auto",
            audit.Id.ToString("N"),
            outcome switch
            {
                "applied" => "Nota e feedback aprovados foram observados no Moodle para este item. A operação não foi reenviada; o estado de liberação/workflow não foi verificado.",
                "conflict" => "O estado observado diverge do destino e do baseline aprovados. O item continua incerto e não foi reenviado.",
                _ => "A leitura não provou a aplicação nem a não aplicação. O item continua incerto e não foi reenviado."
            },
            gradingItemId,
            outcome,
            evidence);
    }

    private async Task TryFinalizeReconciledActionAsync(
        PendingMoodleAction action,
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        if (action.Status != PendingActionStatus.ExecutionUnknown || gradingRepository is null)
        {
            return;
        }

        var statuses = await gradingRepository.GetPublicationClaimStatusesAsync(publicationId, cancellationToken);
        if (statuses.Count == 0 || statuses.Values.Any(status => status is "AwaitingConfirmation" or "Authorized" or "Executing" or "ExecutionUnknown"))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var audit = new MoodleAuditLog
        {
            CorrelationId = action.CorrelationId,
            ToolName = "moodle_reconcile_write",
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            ActorSubject = currentUser.Subject,
            ActorEmail = currentUser.Email,
            ActorMoodleUserId = action.CreatedByMoodleUserId,
            CourseId = action.CourseId,
            MoodleFunction = "mod_assign_save_grade",
            PendingActionId = action.Id,
            StartedAt = now,
            FinishedAt = now,
            DurationMs = 0,
            RequestSanitizedJson = AuditPayloadSanitizer.SerializeSanitized(new
            {
                actionId = action.Id,
                publicationId,
                resolution = "all_items_terminal"
            }),
            ResponseSummaryJson = JsonSerializer.Serialize(new { itemCount = statuses.Count }, JsonOptions),
            Status = "grading_publication_items_reconciled"
        };
        await pendingActions.TryResolveExecutionUnknownWithAuditAsync(
            action.Id,
            PendingActionStatus.Executed,
            audit,
            cancellationToken);
    }

    private static GradingLaunchPayload DeserializeGradingPayload(JsonElement payloadRoot)
    {
        try
        {
            return JsonSerializer.Deserialize<GradingLaunchPayload>(payloadRoot.GetRawText(), JsonOptions)
                ?? throw new InvalidOperationException("O payload de publicação está vazio.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("O payload da publicação não contém uma seleção de correção válida.");
        }
    }

    private static string ResolvePublicationConnectionKey(AssistedGradingBatch batch) =>
        !string.IsNullOrWhiteSpace(batch.MoodleConnectionId)
            ? $"connection:{batch.MoodleConnectionId.Trim()}"
            : $"client:{(string.IsNullOrWhiteSpace(batch.ConnectorClientId) ? "default" : batch.ConnectorClientId)}" +
              $":alias:{(string.IsNullOrWhiteSpace(batch.ConnectionAlias) ? "default" : batch.ConnectionAlias)}";
}
