using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;
using Moodle.Presentation.Tools;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Tools.Grading;

public sealed record GradingCorrectionsCsvExportResult(
    [property: JsonPropertyName("batchJobId")] Guid BatchJobId,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("fileSizeBytes")] long FileSizeBytes,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("generatedItems")] int GeneratedItems,
    [property: JsonPropertyName("pendingItems")] int PendingItems,
    [property: JsonPropertyName("blockedItems")] int BlockedItems,
    [property: JsonPropertyName("columns")] IReadOnlyList<string> Columns);

[McpServerToolType]
public sealed class MoodleGradingTools(
    IMediator mediator,
    IMoodleConnectionSelection moodleSelection,
    IMoodleUserResolver moodleUserResolver,
    MoodleSnapshotToolContext? snapshotContext = null,
    IOptions<GradingLimitsOptions>? gradingLimits = null,
    IMoodleCredentialsProvider? credentialsProvider = null)
{
    private readonly GradingLimitsOptions _gradingLimits = gradingLimits?.Value ?? new GradingLimitsOptions();

    [McpServerTool(
        Name = "start_pending_grading_run",
        Title = "Start Pending Grading Run",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<StartPendingGradingRunResult>))]
    [Description("Inicia o fluxo de correcao de entregas pendentes. Quando courseId for informado, limita toda a descoberta e os sublotes a esse curso; quando omitido, percorre os cursos acessiveis. Nao escreve no Moodle. Use o gradingRunId retornado para paginar, preparar e salvar todos os sublotes; batchJobIds continuam disponiveis para compatibilidade. Em seguida escolha um destino: export_grading_corrections_csv para CSV externo ou create_batch_grade_launch_preview para revisar a publicacao no Moodle. Se o usuario tiver pedido explicitamente uma reavaliacao, use includeAlreadyGraded=true para incluir submissaoes ja corrigidas; a sobrescrita no Moodle continua exigindo allowOverwriteExisting=true na previa, que deve ser apresentada antes de seguir a aprovacao do usuario.")]
    public async Task<CallToolResult> IniciarFluxoCorrecaoPendentesAsync(
        [Description("Identificador opcional do curso a processar. Quando informado, nenhum outro curso e consultado.")]
        string? courseId = null,
        [Description("IDs opcionais das atividades a processar dentro do curso. Aceita instanceId ou cmid; quando omitido, inclui todas as atividades avaliativas.")]
        string[]? assignmentIds = null,
        [Description("Opcional: array JSON não vazio de IDs de submissão. Exige courseId, exatamente um assignmentId/cmid e idempotencyKey. Omita o campo para a varredura normal; null, array vazio ou IDs inválidos são rejeitados.")]
        string[] submissionIds = null!,
        [Description("Chave estável para repetir a mesma seleção após timeout. Reutilizar a chave com outra seleção/configuração conflita; uma nova chave representa uma nova tentativa intencional.")]
        string? idempotencyKey = null,
        [Description("Numero maximo de cursos a percorrer. Use 0 para todos os cursos acessiveis.")]
        int maxCourses = 0,
        [Description("Numero maximo de entregas por sublote, de 1 a 400. A ferramenta cria sublotes adicionais ate percorrer todas as entregas pendentes.")]
        int maxItemsPerBatch = 400,
        [Description("Quando true, inclui contexto de rubrica na montagem dos sublotes.")]
        bool includeRubric = true,
        [Description("Padrão true: inclui os arquivos originais das entregas no contexto de correção. Quando false, omite anexos e retorna aviso; não atribua nota nem feedback conclusivo se a evidência restante for insuficiente.")]
        bool includeSubmissionFiles = true,
        [Description("Quando true, inclui materiais auxiliares do curso. Mesmo quando false, o conector procura automaticamente arquivos de enunciado cujo nome/número corresponda à atividade; materiais genéricos só entram quando esta opção está ativa.")]
        bool includeCourseMaterials = false,
        [Description("Instrucoes adicionais do professor/tutor para orientar a correcao.")]
        string? teacherInstructions = null,
        [Description("Prioridade sugerida: low, normal ou high.")]
        string priority = "normal",
        [Description("Use true somente quando o usuario pedir explicitamente para reavaliar correcoes ja publicadas. Consulta mod_assign_get_submissions sem filtrar gradingstatus e cria novos itens de correcao para as submissaoes entregues, sem alterar o Moodle nesta etapa.")]
        bool includeAlreadyGraded = false,
        [Description("Alias do Moodle a consultar. Quando omitido, usa o Moodle padrao do usuario.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        if (submissionIds is not null)
        {
            if (submissionIds.Length == 0)
            {
                return ToolResultHelper.Error<StartPendingGradingRunResult>(
                    "submissionIds não pode ser um array vazio; omita o campo para iniciar a varredura normal.");
            }

            if (submissionIds.Any(string.IsNullOrWhiteSpace))
            {
                return ToolResultHelper.Error<StartPendingGradingRunResult>(
                    "submissionIds deve conter somente IDs de submissão em texto não vazio.");
            }
        }

        return await StartPendingGradingRunCoreAsync(
            courseId,
            maxCourses,
            maxItemsPerBatch,
            includeRubric,
            includeSubmissionFiles,
            includeCourseMaterials,
            teacherInstructions,
            priority,
            includeAlreadyGraded,
            moodleAlias,
            assignmentIds,
            submissionIds,
            idempotencyKey,
            cancellationToken);
    }

    [McpServerTool(
        Name = "requeue_blocked_grading_items",
        Title = "Requeue Blocked Grading Items",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<RequeueBlockedGradingItemsResult>))]
    [Description("Reabre somente os itens de analise informados que estao bloqueados ou falharam, devolvendo-os para a IA. Nao altera o Moodle, nao reabre itens publicados nem itens com revisao final; exige os gradingItemIds explicitamente. Para falha de publicacao de uma revisao final, use requeue_failed_grading_publication_items.")]
    public async Task<CallToolResult> ReenfileirarItensBloqueadosAsync(
        [Description("Identificador do lote original ou gradingRunId agregado.")]
        Guid batchJobId,
        [Description("IDs dos itens bloqueados a reprocessar; nunca informe itens ja publicados.")]
        Guid[] gradingItemIds,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty)
        {
            return ToolResultHelper.Error<RequeueBlockedGradingItemsResult>("Informe um identificador de lote valido.");
        }

        if (gradingItemIds.Length == 0)
        {
            return ToolResultHelper.Error<RequeueBlockedGradingItemsResult>("Informe pelo menos um gradingItemId para reprocessar.");
        }

        RequeueBlockedGradingItemsResult data;
        try
        {
            data = await mediator.Send(
                new RequeueBlockedGradingItemsCommand(batchJobId, gradingItemIds),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<RequeueBlockedGradingItemsResult>(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResultHelper.Error<RequeueBlockedGradingItemsResult>(ex.Message);
        }
        catch
        {
            return ToolResultHelper.Error<RequeueBlockedGradingItemsResult>("Nao foi possivel reabrir os itens bloqueados neste momento.");
        }

        var response = new ToolResponse<RequeueBlockedGradingItemsResult>(
            data.RequeuedItems > 0 || data.AlreadyQueuedItems > 0 ? "ok" : "partial_failure",
            data,
            data.Failures.Select(failure => failure.Message).ToArray(),
            AuditId: null,
            DateTimeOffset.UtcNow);
        return new CallToolResult
        {
            Content = [new TextContentBlock
            {
                Text = $"{data.RequeuedItems} item(ns) reaberto(s), {data.AlreadyQueuedItems} ja aguardando IA e {data.FailedItems} com falha."
            }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    [McpServerTool(
        Name = "find_grading_correction_by_submission",
        Title = "Find Grading Correction By Submission",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<FindGradingCorrectionBySubmissionResult>))]
    [Description("Localiza somente no ledger local do conector uma correcao anterior pela submissao Moodle. Retorna gradingItemId, batchJobId e gradingRunId quando o lote pertence ao usuario atual; nao consulta nem altera o Moodle. Use esta ferramenta quando uma nova tentativa for barrada por duplicidade e o identificador antigo nao estiver disponivel.")]
    public async Task<CallToolResult> LocalizarCorrecaoPorSubmissaoAsync(
        [Description("submissionId numerico informado pelo Moodle.")]
        string submissionId,
        [Description("courseId opcional para desempatar resultados.")]
        string? courseId = null,
        [Description("assignmentId ou instanceId opcional para desempatar resultados.")]
        string? assignmentId = null,
        [Description("Alias do Moodle onde a submissao foi processada.")]
        string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(submissionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSubmissionId) ||
            parsedSubmissionId <= 0)
        {
            return ToolResultHelper.Error<FindGradingCorrectionBySubmissionResult>("Informe um submissionId numerico positivo.");
        }

        var courseIsValid = TryParseOptionalPositiveId(courseId, "courseId", out var parsedCourseId, out var courseError);
        var assignmentIsValid = TryParseOptionalPositiveId(assignmentId, "assignmentId", out var parsedAssignmentId, out var assignmentError);
        if (!courseIsValid || !assignmentIsValid)
        {
            return ToolResultHelper.Error<FindGradingCorrectionBySubmissionResult>(courseError ?? assignmentError!);
        }

        moodleSelection.Alias = moodleAlias;
        MoodleCredentials? credentials = null;
        if (credentialsProvider is not null)
        {
            try
            {
                credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A local lookup remains useful even when the current
                // connection cannot be resolved; the handler still scopes by
                // the authenticated connector subject.
            }
        }

        try
        {
            var data = await mediator.Send(
                new FindGradingCorrectionBySubmissionQuery(
                    parsedSubmissionId,
                    parsedCourseId,
                    parsedAssignmentId,
                    credentials?.ConnectionId,
                    credentials?.ClientId,
                    credentials?.Alias),
                cancellationToken);
            var response = new ToolResponse<FindGradingCorrectionBySubmissionResult>(
                data.Found ? "ok" : "not_found",
                data,
                [],
                AuditId: null,
                DateTimeOffset.UtcNow);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = data.Message }],
                StructuredContent = JsonSerializer.SerializeToElement(response),
                IsError = false
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<FindGradingCorrectionBySubmissionResult>(ex.Message);
        }
        catch
        {
            return ToolResultHelper.Error<FindGradingCorrectionBySubmissionResult>("Nao foi possivel consultar o ledger de correcoes neste momento.");
        }
    }

    [McpServerTool(
        Name = "requeue_failed_grading_publication_items",
        Title = "Requeue Failed Grading Publication Items",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<RequeueFailedGradingPublicationItemsResult>))]
    [Description("Recupera itens revisados cuja publicacao no Moodle falhou de forma comprovada. Mantem nota e feedback finais, recusa execucao desconhecida e nao escreve no Moodle; depois gere uma nova previa e confirme novamente. Use find_grading_correction_by_submission quando voce so tiver o submissionId.")]
    public async Task<CallToolResult> ReenfileirarFalhasDePublicacaoAsync(
        [Description("batchJobId ou gradingRunId que contem os itens.")]
        Guid batchJobId,
        [Description("IDs dos itens cuja publicacao falhou; informe-os explicitamente.")]
        Guid[] gradingItemIds,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty || gradingItemIds.Length == 0)
        {
            return ToolResultHelper.Error<RequeueFailedGradingPublicationItemsResult>("Informe um lote valido e pelo menos um gradingItemId.");
        }

        try
        {
            var data = await mediator.Send(
                new RequeueFailedGradingPublicationItemsCommand(batchJobId, gradingItemIds),
                cancellationToken);
            var response = new ToolResponse<RequeueFailedGradingPublicationItemsResult>(
                data.RequeuedItems > 0 || data.AlreadyQueuedItems > 0 ? "ok" : "partial_failure",
                data,
                data.Failures.Select(failure => failure.Message).ToArray(),
                AuditId: null,
                DateTimeOffset.UtcNow);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = data.NextStep }],
                StructuredContent = JsonSerializer.SerializeToElement(response),
                IsError = false
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<RequeueFailedGradingPublicationItemsResult>(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResultHelper.Error<RequeueFailedGradingPublicationItemsResult>(ex.Message);
        }
        catch
        {
            return ToolResultHelper.Error<RequeueFailedGradingPublicationItemsResult>("Nao foi possivel recuperar as falhas de publicacao neste momento.");
        }
    }

    [McpServerTool(
        Name = "cancel_assisted_grading_batch",
        Title = "Cancel Assisted Grading Batch",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<CancelAssistedGradingBatchResult>))]
    [MoodleToolMetadata(
        Family = "grading",
        Classification = "R3",
        Kind = "destructive-write",
        CanonicalOperation = "grading.cancel_local_batch",
        RequiredPlatformPermission = "tool.assignments.grade",
        Evidence = "Cancela uma execucao local e, somente com confirmacao literal, remove a projeção local de lotes que nunca foram publicados; nunca escreve nem apaga dados no Moodle.")]
    [Description("Cancela uma execucao local de correcao para impedir novos workers e publicacoes pendentes. Por padrao preserva o historico. Para remover os dados locais de uma execucao que nao possui itens publicados, use purgeLocalData=true e informe confirmationText exatamente como CANCELAR_CORRECOES_LOCAIS. Itens publicados, escritas de resultado desconhecido, leases ativos e publicacoes pendentes bloqueiam o expurgo; o Moodle nunca e alterado por esta ferramenta." )]
    public async Task<CallToolResult> CancelarLoteCorrecaoAsync(
        [Description("Identificador do batchJobId legado ou do gradingRunId agregado retornado por start_pending_grading_run.")]
        Guid batchJobId,
        [Description("Quando true, remove a projecao local depois dos guardas de seguranca. O padrao e false e apenas cancela.")]
        bool purgeLocalData = false,
        [Description("Obrigatorio quando purgeLocalData=true: CANCELAR_CORRECOES_LOCAIS.")]
        string? confirmationText = null,
        [Description("Motivo operacional para a auditoria local da solicitacao.")]
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty)
        {
            return ToolResultHelper.Error<CancelAssistedGradingBatchResult>("Informe um identificador de lote ou execucao valido.");
        }

        CancelAssistedGradingBatchResult data;
        try
        {
            data = await mediator.Send(
                new CancelAssistedGradingBatchCommand(batchJobId, purgeLocalData, confirmationText, reason),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<CancelAssistedGradingBatchResult>(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResultHelper.Error<CancelAssistedGradingBatchResult>(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ToolResultHelper.Error<CancelAssistedGradingBatchResult>(ex.Message);
        }
        catch
        {
            return ToolResultHelper.Error<CancelAssistedGradingBatchResult>("Nao foi possivel cancelar a execucao local neste momento.");
        }

        var response = new ToolResponse<CancelAssistedGradingBatchResult>(
            data.Purged ? "ok" : "cancelled",
            data,
            data.Warnings ?? [],
            AuditId: null,
            DateTimeOffset.UtcNow);
        var text = data.Purged
            ? $"Execucao cancelada e removida localmente: {data.DeletedItems} item(ns), {data.DeletedBatches} sublote(s) e {data.DeletedRuns} execucao(oes). Nenhuma alteracao foi feita no Moodle."
            : data.Message;
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    [McpServerTool(
        Name = "cancel_grading_batch",
        Title = "Cancel Grading Batch",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<CancelAssistedGradingBatchResult>))]
    [Description("Alias de compatibilidade para cancel_assisted_grading_batch. Aceita batchJobId ou gradingRunId, cancela o processamento local e preserva os guardas contra publicacoes duplicadas.")]
    public Task<CallToolResult> CancelarLoteCorrecaoCompatAsync(
        [Description("batchJobId ou gradingRunId retornado pelo conector.")]
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        return CancelarLoteCorrecaoAsync(batchJobId, false, null, null, cancellationToken);
    }

    [McpServerTool(
        Name = "plan_grading_publication",
        Title = "Plan Grading Publication",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GradingPublicationPlanResult>))]
    [MoodleToolMetadata(
        Family = "grading",
        Classification = "R3",
        Kind = "read",
        CanonicalOperation = "grading.publication.plan",
        Structural = false,
        Evidence = "Agrupa localmente apenas itens elegiveis por atividade; nao consulta o Moodle, nao cria previa e nao autoriza escrita.")]
    [Description("Planeja grupos de publicacao somente com o ledger local, agrupados por atividade e limitados pelo tamanho recomendado configurado. Use cada grupo em uma chamada independente a create_batch_grade_launch_preview com seus gradingItemIds; apresente todas as paginas de cada previa e obtenha aprovacao para a previa exibida antes de confirmar. O limite recomendado e uma hipotese para homologacao, nao um limite do Moodle. Retorna referencias de acoes ativas acessiveis; acompanhe essas acoes antes de criar novos grupos para os itens reservados. O plano e um retrato local: a previa revalida estado remoto, versao, tentativa, permissoes e claims. Nao cria previas nem autoriza/publica escritas. Pagine com page/pageSize, no maximo 25 grupos por pagina.")]
    public async Task<CallToolResult> PlanejarPublicacaoCorrecaoAsync(
        [Description("batchJobId ou gradingRunId retornado por start_pending_grading_run.")]
        Guid batchJobId,
        [Description("Pagina iniciando em 1.")]
        int page = 1,
        [Description("Grupos por pagina, de 1 a 25.")]
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty)
        {
            return ToolResultHelper.Error<GradingPublicationPlanResult>("Informe um identificador de lote ou execucao valido.");
        }

        try
        {
            var data = await mediator.Send(
                new GetGradingPublicationPlanQuery(batchJobId, page, pageSize),
                cancellationToken);
            var response = new ToolResponse<GradingPublicationPlanResult>(
                "ok",
                data,
                [],
                AuditId: null,
                DateTimeOffset.UtcNow);
            var groupSummary = data.Groups.Count == 0
                ? "nenhum grupo elegivel nesta pagina"
                : string.Join("; ", data.Groups.Select(group =>
                    $"atividade {group.AssignmentId}, grupo {group.GroupIndex}, {group.GradingItemIds.Count} item(ns)"));
            var actionSummary = data.ActiveActions.Count == 0
                ? string.Empty
                : $" Acoes ativas acessiveis para acompanhar: {string.Join(", ", data.ActiveActions.Select(action => action.PendingActionId))}.";
            var narration = $"Plano local: {data.TotalGroups} grupo(s), pagina {data.Page}/{Math.Max(1, (data.TotalGroups + data.PageSize - 1) / data.PageSize)}; {groupSummary}. Use create_batch_grade_launch_preview com os gradingItemIds de cada grupo. O plano nao autoriza escrita; cada previa ainda deve ser apresentada e aprovada antes da confirmacao.{actionSummary}";
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = narration + "\n\n" + JsonSerializer.Serialize(response) }],
                StructuredContent = JsonSerializer.SerializeToElement(response),
                IsError = false
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            return ToolResultHelper.Error<GradingPublicationPlanResult>(exception, errorPhase: "query", nextAction: "review_access_and_retry_status_read");
        }
        catch (InvalidOperationException exception)
        {
            return ToolResultHelper.Error<GradingPublicationPlanResult>(exception, errorPhase: "query", nextAction: "retry_status_read");
        }
        catch
        {
            return ToolResultHelper.Error<GradingPublicationPlanResult>("Nao foi possivel planejar a publicacao neste momento.", errorPhase: "query", nextAction: "retry_status_read");
        }
    }

    [McpServerTool(
        Name = "create_batch_grade_launch_preview",
        Title = "Create Batch Grade Launch Preview",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<CreateGradingLaunchPreviewResult>))]
    [Description("Prepara ou recupera uma previa revisavel para publicar as correcoes salvas no Moodle. Aceita rascunhos de IA e correcoes revisadas, sem escrever no Moodle. Para publicar por atividade, chame primeiro plan_grading_publication, leia todas as paginas de grupos e passe os gradingItemIds de um unico grupo por acao; confirme as acoes em sequencia. O recommendedMaxItems e uma hipotese para homologacao, nao limite do Moodle. Retorna a primeira pagina (ate 25 itens) com aluno, atividade, nota, feedback, revisao e avisos; se hasMore=true, leia as paginas restantes por get_grading_publication_status antes de apresentar a previa completa. Repetir o mesmo escopo e versoes reutiliza a acao. Depois da aprovacao afirmativa do usuario, confirm_batch_grade_launch recebe somente pendingActionId.")]
    public Task<CallToolResult> CriarPreviaLancamentoLoteAsync(
        [Description("Identificador do lote de correcao assistida ou do gradingRunId agregado retornado por start_pending_grading_run.")]
        Guid batchJobId,
        [Description("Itens especificos a incluir. Quando vazio, inclui todos os itens prontos do lote.")]
        Guid[]? gradingItemIds = null,
        [Description("Quando true, inclui somente itens ja revisados. O padrao inclui rascunhos salvos para que a confirmacao humana seja a revisao final antes da publicacao.")]
        bool onlyReviewed = false,
        [Description("Quando true, a confirmacao autoriza explicitamente sobrescrever notas e feedbacks que ja existem no Moodle.")]
        bool allowOverwriteExisting = false,
        [Description("Quando true, permite criar previa global mesmo se a cobertura declarada do gradingRun estiver incompleta. Use somente apos revisar expectedItems, preparedItems, missingItems e missingBatchCount.")]
        bool allowPartial = false,
        CancellationToken cancellationToken = default)
    {
        return CreateLaunchPreviewCoreAsync(
            batchJobId,
            gradingItemIds ?? [],
            onlyReviewed,
            allowOverwriteExisting,
            allowPartial,
            cancellationToken);
    }

    [McpServerTool(
        Name = "confirm_batch_grade_launch",
        Title = "Confirm Batch Grade Launch",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<ConfirmMoodleBatchLaunchResult>))]
    [Description("Autoriza a publicacao duravel da previa identificada depois que o usuario der uma aprovacao afirmativa a essa previa. Nao exige frase literal; envie somente pendingActionId. A chamada e idempotente e assincrona: authorized nao significa publicado. Depois acompanhe get_grading_publication_status por pendingActionId ou gradingRunId; so considere efetivo o item com commitStatus=Succeeded. O worker revalida rascunho, submissao, notas existentes e tentativa antes da escrita. Se um item ficar execution_unknown, chame moodle_reconcile_write com o pendingActionId, o gradingItemId exato e resolution=auto antes de preparar qualquer nova previa. A tool relê tentativa, nota e feedback; não envia POST. Respostas negativas ou ambiguas nunca devem chamar esta ferramenta.")]
    public Task<CallToolResult> ConfirmarLancamentoLoteMoodleAsync(
        [Description("Identificador da acao pendente retornada por criar_previa_lancamento_lote.")]
        Guid pendingActionId,
        [Description("Opcional, legado. A publicacao de notas nao exige texto literal; mantenha vazio ou omita.")]
        string? confirmationText = null,
        CancellationToken cancellationToken = default)
    {
        return ConfirmLaunchCoreAsync(pendingActionId, confirmationText, cancellationToken);
    }

    [McpServerTool(
        Name = "get_assisted_grading_batch_status",
        Title = "Get Assisted Grading Batch Status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<AssistedGradingBatchStatusResult>))]
    [Description("Consulta o ledger local da correcao, inclusive antes/depois da aprovacao. Use o batchJobId/gradingRunId retornado pela execucao ou confirmacao. Retorna por item status operacional, publicationState e failureReason; publicationActions lista cada pendingActionId para consultar a previa correspondente. suggestedAction aponta para uma tool existente quando a combinacao de estados permite recomendacao segura; suggestedActionLimitation explica estados legados/bloqueados sem acao automatica. Resultado desconhecido recomenda moodle_reconcile_write; nunca crie nova previa antes da reconciliacao. So considere publicada a nota de um item com commitStatus=Succeeded. Nao escreve no Moodle.")]
    public async Task<CallToolResult> ConsultarStatusLoteCorrecaoAsync(
        [Description("batchJobId ou gradingRunId retornado por start_pending_grading_run ou create_batch_grade_launch_preview.")]
        Guid batchJobId,
        [Description("Pagina iniciando em 1.")]
        int page = 1,
        [Description("Itens por pagina, de 1 a 100.")]
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty)
        {
            return ToolResultHelper.Error<AssistedGradingBatchStatusResult>("Informe um identificador de lote valido.");
        }

        try
        {
            var data = await mediator.Send(
                new GetAssistedGradingBatchStatusQuery(batchJobId, page, pageSize),
                cancellationToken);
            var response = new ToolResponse<AssistedGradingBatchStatusResult>(
                "ok",
                data,
                [],
                AuditId: null,
                DateTimeOffset.UtcNow);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = BuildAssistedGradingBatchStatusNarration(data) }],
                StructuredContent = JsonSerializer.SerializeToElement(response),
                IsError = false
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<AssistedGradingBatchStatusResult>(ex, errorPhase: "query", nextAction: "retry_status_read");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResultHelper.Error<AssistedGradingBatchStatusResult>(ex, errorPhase: "query", nextAction: "review_access_and_retry_status_read");
        }
        catch
        {
            return ToolResultHelper.Error<AssistedGradingBatchStatusResult>("Nao foi possivel consultar o status da correcao neste momento.", errorPhase: "query", nextAction: "retry_status_read");
        }
    }

    [McpServerTool(
        Name = "get_grading_batch_audit",
        Title = "Get Grading Batch Audit",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GradingAuditResult>))]
    [Description("Consulta o historico privado de auditoria de um lote de correcao, incluindo revisoes humanas anteriores e atuais. O acesso e limitado ao criador do lote ou a um administrador autorizado. Informe batchJobId e use page/pageSize para paginar. Somente leitura; nao consulta nem altera o Moodle.")]
    public async Task<CallToolResult> ConsultarAuditoriaLoteCorrecaoAsync(
        [Description("Identificador do lote de correcao cujo historico deve ser consultado.")]
        Guid batchJobId,
        [Description("Pagina iniciando em 1.")]
        int page = 1,
        [Description("Eventos por pagina, de 1 a 100.")]
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty)
        {
            return ToolResultHelper.Error<GradingAuditResult>("Informe um identificador de lote valido.");
        }

        try
        {
            var data = await mediator.Send(
                new GetGradingBatchAuditQuery(batchJobId, page, pageSize),
                cancellationToken);
            var response = new ToolResponse<GradingAuditResult>(
                "ok",
                data,
                [],
                AuditId: null,
                DateTimeOffset.UtcNow);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Historico de auditoria: {data.TotalEvents} evento(s) no total; exibindo {data.Events.Count} nesta pagina." }],
                StructuredContent = JsonSerializer.SerializeToElement(response),
                IsError = false
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<GradingAuditResult>(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResultHelper.Error<GradingAuditResult>(ex.Message);
        }
        catch
        {
            return ToolResultHelper.Error<GradingAuditResult>("Nao foi possivel consultar a auditoria da correcao neste momento.");
        }
    }

    [McpServerTool(
        Name = "get_grading_publication_status",
        Title = "Get Grading Publication Status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GradingPublicationStatusResult>))]
    [Description("Recupera uma previa de publicacao ja salva, mesmo apos perder o contexto da conversa. Informe pendingActionId ou gradingRunId. Por run, seleciona a acao mais recente ainda ativa e retorna relatedPendingActionIds; consulte cada pendingActionId para preservar o resultado individual de todas as acoes. Por pendingActionId, retorna exatamente aquela previa. O resultado e paginado e inclui feedback integral, estados do ledger e codigos estaveis de falha. Authorized/queued ainda nao significa publicado: so considere concluido quando publicationState=succeeded e commitStatus=Succeeded. ExecutionUnknown exige moodle_reconcile_write por item (`pendingActionId`, `gradingItemId`, `resolution='auto'`) antes de qualquer nova previa; inconclusivo ou conflito permanece incerto. Somente leitura.")]
    public async Task<CallToolResult> ConsultarStatusPublicacaoCorrecaoAsync(
        [Description("ID da previa, quando conhecido. Informe somente um entre pendingActionId e gradingRunId.")]
        Guid? pendingActionId = null,
        [Description("ID da execucao de correcao para recuperar a previa mais recente sem conhecer o pendingActionId.")]
        Guid? gradingRunId = null,
        [Description("Pagina iniciando em 1.")]
        int page = 1,
        [Description("Itens por pagina, de 1 a 100. O padrao 25 coincide com a pagina inicial da previa.")]
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if ((pendingActionId is null || pendingActionId == Guid.Empty) ==
            (gradingRunId is null || gradingRunId == Guid.Empty))
        {
            return ToolResultHelper.Error<GradingPublicationStatusResult>(
                "Informe exatamente um identificador: pendingActionId ou gradingRunId.");
        }

        try
        {
            var data = await mediator.Send(
                new GetGradingPublicationStatusQuery(pendingActionId, gradingRunId, page, pageSize),
                cancellationToken);
            var response = new ToolResponse<GradingPublicationStatusResult>(
                "ok",
                data,
                [],
                AuditId: null,
                DateTimeOffset.UtcNow);
            return new CallToolResult
            {
                Content = [new TextContentBlock
                {
                    Text = $"Publicacao {data.PendingActionId}: {data.PublicationState}; proxima acao {data.NextAction}; {data.PublicationCounts.Ready} pronto(s), {data.PublicationCounts.Succeeded} concluido(s), {data.PublicationCounts.Unknown} desconhecido(s), {data.PublicationCounts.Blocked} bloqueado(s), pagina {data.Page}/{Math.Max(1, (data.PreviewItemCount + data.PageSize - 1) / data.PageSize)}." +
                            (data.RelatedPendingActionIds.Count > 1
                                ? $" Esta execucao tem varias acoes; consulte cada pendingActionId: {string.Join(", ", data.RelatedPendingActionIds)}."
                                : string.Empty) +
                            "\n\n" +
                            JsonSerializer.Serialize(response)
                }],
                StructuredContent = JsonSerializer.SerializeToElement(response),
                IsError = false
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResultHelper.Error<GradingPublicationStatusResult>(ex, errorPhase: "query", nextAction: "review_access_and_retry_status_read");
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<GradingPublicationStatusResult>(ex, errorPhase: "query", nextAction: "retry_status_read");
        }
        catch
        {
            return ToolResultHelper.Error<GradingPublicationStatusResult>("Nao foi possivel recuperar o estado salvo da publicacao.", errorPhase: "query", nextAction: "retry_status_read");
        }
    }

    [McpServerTool(
        Name = "refresh_grading_scale",
        Title = "Refresh Grading Scale",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<RefreshGradingScaleResult>))]
    [MoodleToolMetadata(
        Family = "grading",
        Classification = "R3",
        Kind = "draft-write",
        CanonicalOperation = "grading.scale.refresh",
        RequiredPlatformPermission = "tool.assignments.grade",
        Evidence = "Atualiza localmente snapshots selecionados com a escala Moodle fresca, invalida os rascunhos afetados e nunca publica nota ou feedback.")]
    [Description("Atualiza a escala Moodle somente nos gradingItemIds explicitamente informados. Obtenha contextVersion, contextHash e draftVersionHash em get_assisted_grading_batch_status e envie os tres valores por item. O conector consulta settings sem cache e o item exato do gradebook; conflito ou indisponibilidade nao altera os itens. Rascunhos e revisoes nao publicados dos itens alterados sao preservados na auditoria privada e reprocessados; itens publicados/desconhecidos bloqueiam. Previews pendentes que incluem um alvo alterado sao invalidados. Nenhuma escrita Moodle e realizada; depois consulte o status, revise os novos rascunhos e crie uma previa de publicacao nova.")]
    public async Task<CallToolResult> AtualizarEscalaCorrecaoAsync(
        [Description("batchJobId legado ou gradingRunId retornado por start_pending_grading_run.")]
        Guid batchJobId,
        [Description("ID numerico exato da atividade Moodle (assign instanceId), nao cmid.")]
        string assignmentId,
        [Description("Lista explícita de itens: gradingItemId, contextVersion, contextHash e draftVersionHash lidos mais recentemente no status do lote.")]
        GradingScaleRefreshItemExpectation[] items,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty ||
            !long.TryParse(assignmentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAssignmentId) ||
            parsedAssignmentId <= 0)
        {
            return ToolResultHelper.Error<RefreshGradingScaleResult>("Informe batchJobId e assignmentId numerico valido.");
        }

        if (items is null || items.Length == 0)
        {
            return ToolResultHelper.Error<RefreshGradingScaleResult>("Informe os itens explicitamente, sem deixar a selecao implicita.");
        }

        try
        {
            var data = await mediator.Send(
                new RefreshGradingScaleCommand(batchJobId, parsedAssignmentId, items),
                cancellationToken);
            var response = new ToolResponse<RefreshGradingScaleResult>(
                "ok",
                data,
                data.InvalidatedPreviewActionIds.Count == 0
                    ? []
                    : ["Uma ou mais previas pendentes foram invalidadas e precisam ser refeitas."],
                AuditId: null,
                DateTimeOffset.UtcNow);
            var text = data.Status == "already_current"
                ? "A escala atual ja coincide com todos os itens selecionados; nenhum rascunho foi alterado."
                : $"Escala atualizada em {data.RefreshedItems.Count} item(ns); {data.UnchangedItems.Count} ja estavam atualizados. Revise novamente e crie uma previa nova antes de qualquer publicacao.";
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = text }],
                StructuredContent = JsonSerializer.SerializeToElement(response),
                IsError = false
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResultHelper.Error<RefreshGradingScaleResult>(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<RefreshGradingScaleResult>(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ToolResultHelper.Error<RefreshGradingScaleResult>(ex.Message);
        }
        catch
        {
            return ToolResultHelper.Error<RefreshGradingScaleResult>("Nao foi possivel atualizar a escala deste item neste momento.");
        }
    }

    // ============================================================
    // Tool: prepare_ai_grading_batch
    // ============================================================

    [McpServerTool(
        Name = "prepare_ai_grading_batch",
        Title = "Prepare AI Grading Batch",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<AiGradingBatchPackageResult>))]
    [Description("Retorna uma pagina da correcao assistida com criterios, escala, resources de submissao e contexto compartilhado. As referencias de resource permanecem apenas no structuredContent e nao sao anexadas automaticamente ao content da tool; leia os bytes explicitamente com resources/read ou read_moodle_resource quando necessario. Use page/nextPage ate hasMore=false; totalItems e eligibleItems cobrem o escopo inteiro, expectedItems/preparedItems/missingItems mostram a cobertura. Para mais de um item elegivel e cliente que abre ZIP, prepare uma vez por gradingRunId com prepare_grading_packages(packageMode=per_activity), acompanhe get_grading_package_status e leia cada parte pronta. Importe resultados de ZIP pelo schema com import_grading_package_results(packageId, manifestHash, resultsJson), que valida item, hash e evidencia antes de salvar rascunhos. Para item unico, cliente sem suporte a ZIP ou fallback de pacote indisponivel, leia resources originais e salve com save_ai_grading_batch. Conte cobertura pela uniao dos gradingItemIds, sem duplicar itens entre pacotes e fallback. Bytes recebidos nao provam que o cliente extraiu/avaliou os arquivos. proposal.evidence deve apontar para artifactIds reais da submissao; evidencia insuficiente fica para revisao. Se a entrega for de outra atividade, proponha nota 0 quando houver escala e feedback especifico, mantendo revisao humana. Ambos caminhos salvam localmente e nao publicam. Depois use export_grading_corrections_csv se o usuario pediu CSV ou create_batch_grade_launch_preview para revisar publicacao.")]
    public async Task<CallToolResult> PrepararLoteCorrecaoIaAsync(
        [Description("Identificador retornado por start_pending_grading_run: pode ser batchJobId (compatibilidade) ou gradingRunId (recomendado para consolidar todos os sublotes).")]
        Guid batchJobId,
        [Description("Pagina iniciando em 1. Use nextPage quando hasMore=true.")]
        int page = 1,
        [Description("Itens por pagina, de 1 a 400. O padrao 400 limita memoria e tamanho da resposta para execucoes grandes.")]
        int pageSize = 400,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty)
        {
            return ToolResultHelper.Error<AiGradingBatchPackageResult>("Informe um identificador de lote valido.");
        }

        AiGradingBatchPackageResult data;
        try
        {
            data = await mediator.Send(
                new PrepareAiGradingBatchQuery(batchJobId, page, pageSize),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<AiGradingBatchPackageResult>(ex, errorPhase: "package", nextAction: "check_package_status_before_retry");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResultHelper.Error<AiGradingBatchPackageResult>(ex, errorPhase: "package", nextAction: "review_access_and_check_package_status");
        }
        catch
        {
            return ToolResultHelper.Error<AiGradingBatchPackageResult>("Nao foi possivel preparar o pacote IA do lote neste momento.", errorPhase: "package", nextAction: "check_package_status_before_retry");
        }

        var response = new ToolResponse<AiGradingBatchPackageResult>(
            "ok",
            data,
            data.Warnings,
            AuditId: null,
            DateTimeOffset.UtcNow);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = BuildPrepareAiBatchNarration(data) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    // ============================================================
    // Tool: save_ai_grading_batch
    // ============================================================

    [McpServerTool(
        Name = "save_ai_grading_batch",
        Title = "Save AI Grading Batch",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<SaveAiGradingBatchResult>))]
    [Description("Salva nota e feedback gerados pela IA como correcoes internas. O identificador aceita batchJobId legado ou gradingRunId agregado e os itens podem vir de qualquer sublote autorizado da execucao. Nao escreve no Moodle. Depois de salvar, escolha somente um destino: export_grading_corrections_csv para gerar CSV externo ou create_batch_grade_launch_preview para revisar uma publicacao no Moodle.")]
    public async Task<CallToolResult> SalvarCorrecoesIaLoteAsync(
        [Description("Identificador retornado por start_pending_grading_run: batchJobId legado ou gradingRunId agregado (recomendado).")]
        Guid batchJobId,
    [Description("Array de correcoes. O formato legado usa gradingItemId, nota e feedback. Quando disponivel, proposal deve conter a versao/hash do contexto, criterios, evidencias, cobertura e feedback estruturado; em evidence use artifactId real do pacote e descreva a observacao. Entregas incompatíveis devem receber nota 0 quando houver escala e feedback explicativo, permanecendo em revisao humana. Para um rascunho MCP que possa gerar previa de lancamento, proposal.resourceUris deve conter todas e somente as URIs do pacote com resourceType 'submission'; nao inclua recursos de contexto. A escala numerica continua sendo validada pelo Moodle.")]
        AiGradingItemInput[] items,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty)
        {
            return ToolResultHelper.Error<SaveAiGradingBatchResult>("Informe um identificador de lote valido.");
        }

        if (items.Length == 0)
        {
            return ToolResultHelper.Error<SaveAiGradingBatchResult>("Informe pelo menos um item de correcao.");
        }

        SaveAiGradingBatchResult data;
        try
        {
            data = await mediator.Send(
                new SaveAiGradingBatchCommand(batchJobId, items),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<SaveAiGradingBatchResult>(ex, errorPhase: "save", nextAction: "review_input_and_retry_save");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResultHelper.Error<SaveAiGradingBatchResult>(ex, errorPhase: "save", nextAction: "review_access_and_retry_save");
        }
        catch
        {
            return ToolResultHelper.Error<SaveAiGradingBatchResult>("Nao foi possivel salvar as correcoes IA neste momento.", errorPhase: "save", nextAction: "review_input_and_retry_save");
        }

        var response = new ToolResponse<SaveAiGradingBatchResult>(
            data.SavedItems > 0 ? "ok" : "partial_failure",
            data,
            data.Warnings,
            AuditId: null,
            DateTimeOffset.UtcNow);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = BuildSaveAiGradingNarration(data) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            // Falhas de itens são reportadas em warnings/updatedItems para o
            // cliente reconciliar, sem virar RuntimeException no bridge MCP.
            IsError = false
        };
    }

    [McpServerTool(
        Name = "export_grading_corrections_csv",
        Title = "Export Grading Corrections CSV",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GradingCorrectionsCsvExportResult>))]
    [Description("Gera e entrega um CSV UTF-8 com as correcoes locais no formato nome;nota;feedback. O identificador aceita batchJobId legado ou gradingRunId agregado e consolida todos os sublotes autorizados. Use apos save_ai_grading_batch. Esta ferramenta somente le rascunhos locais e nunca confirma nem envia dados ao Moodle.")]
    public async Task<CallToolResult> ExportarCorrecoesCsvAsync(
        [Description("Identificador do lote retornado por start_pending_grading_run.")]
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty)
        {
            return ToolResultHelper.Error<GradingCorrectionsCsvExportResult>("Informe um identificador de lote valido.");
        }

        GradingCorrectionsCsvResult data;
        try
        {
            data = await mediator.Send(
                new GetGradingCorrectionsCsvQuery(batchJobId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MoodleApiException ex)
        {
            return ToolResultHelper.Error<GradingCorrectionsCsvExportResult>(ex);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<GradingCorrectionsCsvExportResult>(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResultHelper.Error<GradingCorrectionsCsvExportResult>(ex.Message);
        }
        catch
        {
            return ToolResultHelper.Error<GradingCorrectionsCsvExportResult>("Nao foi possivel gerar o CSV de correcoes neste momento.");
        }

        var generatedAt = data.GeneratedAt;
        var fileName = $"correcoes_{data.BatchJobId:N}_{generatedAt:yyyyMMdd-HHmmss}.csv";
        var contentType = "text/csv; charset=utf-8";
        var csvBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            .GetBytes(BuildCorrectionsCsv(data.Rows));
        var export = new GradingCorrectionsCsvExportResult(
            data.BatchJobId,
            generatedAt,
            fileName,
            contentType,
            csvBytes.LongLength,
            data.TotalItems,
            data.GeneratedItems,
            data.PendingItems,
            data.BlockedItems,
            ["nome", "nota", "feedback"]);
        var response = new ToolResponse<GradingCorrectionsCsvExportResult>(
            "ok",
            export,
            [],
            AuditId: null,
            DateTimeOffset.UtcNow);
        var resource = BlobResourceContents.FromBytes(
            csvBytes,
            $"mcp://moodle/grading-corrections/{Guid.NewGuid():N}/{fileName}",
            contentType);

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = $"CSV de correcoes gerado: {fileName} ({data.GeneratedItems} gerada(s), {data.PendingItems} pendente(s), {data.BlockedItems} bloqueada(s))."
                },
                new EmbeddedResourceBlock { Resource = resource }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }




    private async Task<CallToolResult> StartPendingGradingRunCoreAsync(
        string? courseId,
        int maxCourses,
        int maxItemsPerBatch,
        bool includeRubric,
        bool includeSubmissionFiles,
        bool includeCourseMaterials,
        string? teacherInstructions,
        string priority,
        bool includeAlreadyGraded,
        string? moodleAlias,
        IReadOnlyList<string>? assignmentIds,
        IReadOnlyList<string>? submissionIds,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        moodleSelection.Alias = moodleAlias;
        var moodleUserId = await moodleUserResolver.ResolveMoodleUserIdAsync(cancellationToken);
        if (moodleUserId is null)
        {
            return ToolResultHelper.Error<StartPendingGradingRunResult>("Usuario nao autenticado para iniciar a correcao de pendencias.");
        }

        Guid? snapshotOwnerId = null;
        string? snapshotClientId = null;
        string? snapshotConnectionAlias = null;
        string? moodleConnectionId = null;
        if (submissionIds is not null && credentialsProvider is null)
        {
            return ToolResultHelper.Error<StartPendingGradingRunResult>(
                "A seleção de submissões exige a identidade da conexão Moodle atual.");
        }
        if (submissionIds is not null)
        {
            try
            {
                var credentials = await credentialsProvider!.GetCurrentCredentialsAsync(cancellationToken);
                moodleConnectionId = credentials.ConnectionId;
                snapshotClientId = credentials.ClientId;
                snapshotConnectionAlias = credentials.Alias;
                if (string.IsNullOrWhiteSpace(moodleConnectionId))
                {
                    return ToolResultHelper.Error<StartPendingGradingRunResult>(
                        "A seleção de submissões exige uma conexão Moodle identificada.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return ToolResultHelper.Error<StartPendingGradingRunResult>(exception);
            }
        }
        if (snapshotContext is not null)
        {
            try
            {
                var scope = await snapshotContext.TryResolveAsync(moodleAlias, cancellationToken);
                if (scope is not null)
                {
                    snapshotOwnerId = scope.Identity.Id;
                    snapshotClientId = scope.ClientId;
                    snapshotConnectionAlias = scope.ConnectionAlias;
                }
                else if (submissionIds is not null && string.IsNullOrWhiteSpace(snapshotClientId))
                {
                    return ToolResultHelper.Error<StartPendingGradingRunResult>(
                        "A seleção de submissões exige identidade autenticada do cliente para reservar o run.");
                }
            }
            catch
            {
                if (submissionIds is not null && string.IsNullOrWhiteSpace(snapshotClientId))
                {
                    return ToolResultHelper.Error<StartPendingGradingRunResult>(
                        "Não foi possível confirmar a identidade do cliente para a seleção de submissões.");
                }

                // Legacy callers without a local portal identity retain the
                // existing live command path.
            }
        }
        else if (submissionIds is not null && string.IsNullOrWhiteSpace(snapshotClientId))
        {
            return ToolResultHelper.Error<StartPendingGradingRunResult>(
                "A seleção de submissões exige identidade autenticada do cliente para reservar o run.");
        }

        StartPendingGradingRunResult data;
        try
        {
            data = await mediator.Send(
                new StartPendingGradingRunCommand(
                    moodleUserId.Value.ToString(),
                    maxCourses,
                    maxItemsPerBatch,
                    includeRubric,
                    includeSubmissionFiles,
                    includeCourseMaterials,
                    teacherInstructions,
                    priority,
                    UseSubmissionSnapshots: snapshotOwnerId is not null,
                    SnapshotOwnerId: snapshotOwnerId,
                    SnapshotClientId: snapshotClientId,
                    SnapshotConnectionAlias: snapshotConnectionAlias,
                    CourseId: courseId,
                    AssignmentIds: assignmentIds,
                    IncludeAlreadyGraded: includeAlreadyGraded,
                    SubmissionIds: submissionIds,
                    IdempotencyKey: idempotencyKey,
                    MoodleConnectionId: moodleConnectionId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return ToolResultHelper.Error<StartPendingGradingRunResult>(ex.Message);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<StartPendingGradingRunResult>(exception);
        }

        var hasResolutionFailure = data.AssignmentResolution.Any(item => !item.Resolved);
        var hasCourseFailure = data.Courses.Any(course =>
            course.Status is "course_read_failed" or "partial_failure");
        var responseStatus = hasResolutionFailure || hasCourseFailure
            ? "partial_failure"
            : data.Warnings.Count > 0
                ? "ok_with_skips"
                : "ok";
        var response = new ToolResponse<StartPendingGradingRunResult>(
            responseStatus,
            data,
            data.Warnings,
            AuditId: null,
            DateTimeOffset.UtcNow);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = BuildStartPendingGradingRunNarration(data) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }



    /// <summary>
    /// A snapshot accelerates the common path but must never be a prerequisite
    /// for discovering work that needs grading. When it cannot cover the
    /// request, resolve the assignment instances from Moodle and reuse the
    /// authoritative per-assignment reader.
    /// </summary>






    private async Task<CallToolResult> CreateLaunchPreviewCoreAsync(
        Guid batchJobId,
        IReadOnlyList<Guid> gradingItemIds,
        bool onlyReviewed,
        bool allowOverwriteExisting,
        bool allowPartial,
        CancellationToken cancellationToken)
    {
        if (batchJobId == Guid.Empty)
        {
            return ToolResultHelper.Error<CreateGradingLaunchPreviewResult>("Informe um identificador de lote valido.");
        }

        CreateGradingLaunchPreviewResult data;
        try
        {
            data = await mediator.Send(
                new CreateGradingLaunchPreviewCommand(
                    batchJobId,
                    gradingItemIds,
                    onlyReviewed,
                    allowOverwriteExisting,
                    allowPartial),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MoodleApiException ex)
        {
            return ToolResultHelper.Error<CreateGradingLaunchPreviewResult>(ex, errorPhase: "preview", nextAction: "review_and_retry_preview");
        }
        catch
        {
            return ToolResultHelper.Error<CreateGradingLaunchPreviewResult>("Nao foi possivel criar a previa de lancamento neste momento.", errorPhase: "preview", nextAction: "review_and_retry_preview");
        }

        const int previewPageSize = 25;
        var responseData = data with
        {
            Launches = data.Launches.Take(previewPageSize).ToArray(),
            Page = 1,
            PageSize = previewPageSize,
            PreviewItemCount = data.Launches.Count,
            HasMore = data.Launches.Count > previewPageSize,
            NextAction = data.PendingActionId == Guid.Empty
                ? "attention"
                : data.PublicationState switch
                {
                    "awaiting_approval" => "confirm",
                    "queued" or "executing" or "retry_scheduled" => "wait",
                    "succeeded" => "done",
                    _ => "attention"
                }
        };
        var responseStatus = responseData.PendingActionId == Guid.Empty
            ? "blocked"
            : responseData.PublicationState switch
            {
                "queued" => "authorized",
                "executing" => "executing",
                "retry_scheduled" => "retry_scheduled",
                "unknown" => "unknown",
                "failed" => "failed",
                _ => "pending_confirmation"
            };
        var response = new ToolResponse<CreateGradingLaunchPreviewResult>(
            responseStatus,
            responseData,
            responseData.Warnings,
            AuditId: null,
            DateTimeOffset.UtcNow);

        return new CallToolResult
        {
            Content = [new TextContentBlock
            {
                Text = BuildLaunchPreviewNarration(responseData) +
                       "\n\nDADOS_ESTRUTURADOS:\n" +
                       JsonSerializer.Serialize(response)
            }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            // Ausencia de itens prontos e um estado de negocio recuperavel, nao
            // um argumento invalido da chamada MCP.
            IsError = false
        };
    }

    private async Task<CallToolResult> ConfirmLaunchCoreAsync(
        Guid pendingActionId,
        string? confirmationText,
        CancellationToken cancellationToken)
    {
        if (pendingActionId == Guid.Empty)
        {
            return ToolResultHelper.Error<ConfirmMoodleBatchLaunchResult>("Informe uma acao pendente valida.");
        }

        ConfirmMoodleBatchLaunchResult data;
        try
        {
            data = await mediator.Send(
                new ConfirmMoodleBatchLaunchCommand(
                    pendingActionId,
                    confirmationText,
                    ExecuteImmediately: false),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MoodleApiException ex)
        {
            return ToolResultHelper.Error<ConfirmMoodleBatchLaunchResult>(ex, errorPhase: "publication", nextAction: "consult_publication_status");
        }
        catch (InvalidOperationException ex)
        {
            return ToolResultHelper.Error<ConfirmMoodleBatchLaunchResult>(ex, errorPhase: "publication", nextAction: "consult_publication_status");
        }
        catch
        {
            return ToolResultHelper.Error<ConfirmMoodleBatchLaunchResult>("Nao foi possivel confirmar o lancamento no Moodle neste momento.", errorPhase: "publication", nextAction: "consult_publication_status");
        }

        var response = new ToolResponse<ConfirmMoodleBatchLaunchResult>(
            data.Status switch
            {
                "authorized" => "authorized",
                "retry_scheduled" => "retry_scheduled",
                "executing" => "executing",
                "execution_unknown" => "unknown",
                _ => data.FailedItems == 0 ? "ok" : "partial_failure"
            },
            data,
            data.Failures
                .Select(failure => failure.Message)
                .Concat(data.Warnings ?? [])
                .ToArray(),
            data.AuditId,
            DateTimeOffset.UtcNow);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = BuildConfirmLaunchNarration(data) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            // Falhas por item sao um resultado de negocio concluido e precisam chegar
            // ao cliente no StructuredContent. IsError fica reservado a falhas da tool.
            IsError = false
        };
    }



    private static string BuildStartPendingGradingRunNarration(StartPendingGradingRunResult response)
    {
        if (response.Batches.Count == 0)
        {
            return response.ExistingCorrections.Count > 0
                ? $"Nenhum sublote novo foi criado em {response.CoursesScanned} curso(s): a submissao ja possui correcao local. Consulte existingCorrections para usar o batchJobId/gradingRunId anterior."
                : $"Nenhuma entrega pendente elegivel foi encontrada em {response.CoursesScanned} curso(s).";
        }

        return $"Fluxo de correcoes pendentes iniciado (gradingRunId: {response.GradingRunId}): {response.TotalItems} entrega(s) em {response.CoursesScanned} curso(s), " +
               $"distribuidas em {response.Batches.Count} sublote(s). " +
               "Use o gradingRunId para percorrer as paginas sem interromper o fluxo pelos cursos ou itens bloqueados.";
    }








    private static string BuildLaunchPreviewNarration(CreateGradingLaunchPreviewResult response)
    {
        if (response.PendingActionId == Guid.Empty)
        {
            if (!response.DecisionSafe)
            {
                return $"Previa global bloqueada por cobertura incompleta: esperados {response.ExpectedItems}, preparados {response.PreparedItems}, ausentes {response.MissingItems} em {response.MissingBatchCount} sublote(s). Corrija a cobertura ou use allowPartial=true de forma explicita.";
            }

            if (response.BlockedReasons?.ContainsKey("active_publication_exists") == true)
            {
                return "Uma publicacao ativa ja reserva parte deste escopo. Nao crie outra previa; consulte get_assisted_grading_batch_status com o batchJobId ou gradingRunId para recuperar o pendingActionId ativo e acompanhe-o em get_grading_publication_status.";
            }

            return "Nenhum item pronto para lancamento foi encontrado.";
        }

        var builder = new StringBuilder();
        var existingValues = response.Launches.Count(item =>
            item.ExistingGrade is not null || item.HasExistingFeedback);
        builder.AppendLine("PREVIA DE PUBLICACAO");
        builder.AppendLine();
        builder.AppendLine($"{response.ReadyItems} correcao(oes) prontas. {response.BlockedItems} bloqueada(s).");
        builder.AppendLine($"{existingValues} item(ns) desta pagina ja possuem nota ou feedback informado no Moodle.");
        builder.AppendLine($"Estado da publicacao: {response.PublicationState}.");
        builder.AppendLine($"Proxima acao: {response.NextAction}.");
        builder.AppendLine($"Itens da previa: pagina {response.Page}, mostrando {response.Launches.Count} de {response.PreviewItemCount}.");
        builder.AppendLine("Os nomes, notas e feedbacks abaixo correspondem ao conteudo desta pagina da previa.");
        builder.AppendLine();
        foreach (var item in response.Launches)
        {
            builder.AppendLine($"- Aluno: {item.StudentName ?? item.StudentId}");
            builder.AppendLine($"  Atividade: {item.AssignmentName ?? item.AssignmentId}");
            builder.AppendLine($"  Nota proposta: {(item.Grade?.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) ?? "sem nota")}" +
                               (item.MaxGrade is not null
                                   ? $"/{item.MaxGrade.Value.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"))}"
                                   : string.Empty));
            builder.AppendLine("  Feedback integral:");
            builder.AppendLine(item.FeedbackText);
            builder.AppendLine($"  Revisao: {item.ReviewStatus}");
            builder.AppendLine($"  Situacao: {item.Situation}");
            builder.AppendLine("----------------------------------");
        }

        if (response.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Avisos:");
            foreach (var warning in response.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
        if (response.HasMore)
        {
            builder.Append($"Leia a proxima pagina pela mesma acao antes de apresentar a previa completa ou solicitar aprovacao: get_grading_publication_status(pendingActionId, page={response.Page + 1}).");
        }
        else if (response.PublicationState == "awaiting_approval")
        {
            builder.Append("Se estiver de acordo com este escopo e os avisos, responda que pode publicar.");
        }
        else
        {
            builder.Append("Esta previa ja foi autorizada ou esta em andamento. Consulte get_grading_publication_status para acompanhar; nao solicite nova aprovacao.");
        }
        return builder.ToString();
    }

    private static string BuildAssistedGradingBatchStatusNarration(AssistedGradingBatchStatusResult response)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Status da correcao {response.BatchJobId}: {response.Status}.");
        builder.AppendLine($"Proxima acao: {response.NextAction}.");
        if (response.SuggestedAction is not null)
            builder.AppendLine($"Tool recomendada: {response.SuggestedAction}.");
        if (response.SuggestedActionLimitation is not null)
            builder.AppendLine($"Limite da recomendacao: {response.SuggestedActionLimitation}");
        builder.AppendLine($"Itens processados: {response.ProcessedItems}/{response.TotalItems}. Pagina {response.Page}.");
        if (response.AsOfUtc is not null)
        {
            builder.AppendLine($"Snapshot UTC: {response.AsOfUtc:O}.");
        }
        foreach (var action in response.PublicationActions ?? [])
        {
            builder.AppendLine($"Acao de publicacao {action.PendingActionId}: {action.Status} (expira {action.ExpiresAt:O}).");
            if (action.NextAttemptAt is not null)
                builder.AppendLine($"  retry agendado para {action.NextAttemptAt:O}; nao solicite nova confirmacao.");
        }
        builder.AppendLine();
        foreach (var item in response.Items)
        {
            builder.AppendLine($"- Aluno: {item.StudentName ?? item.StudentId}");
            builder.AppendLine($"  Nota: {(item.Grade?.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) ?? "sem nota")}");
            builder.AppendLine($"  Feedback: {item.FeedbackText ?? "sem feedback"}");
            builder.AppendLine($"  commitStatus: {item.CommitStatus}");
            if (item.PublicationState is not null)
                builder.AppendLine($"  publicationState: {item.PublicationState}");
            if (item.FailureReason is not null)
                builder.AppendLine($"  failureReason: {item.FailureReason}");
        }

        if (response.HasMore)
        {
            builder.AppendLine();
            builder.Append($"Use page={response.Page + 1} para consultar a proxima pagina.");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildConfirmLaunchNarration(ConfirmMoodleBatchLaunchResult response)
    {
        if (response.Status == "authorized")
        {
            var batchHint = response.BatchJobId is Guid batchJobId
                ? $" Consulte get_assisted_grading_batch_status com batchJobId {batchJobId}."
                : string.Empty;
            return "Publicacao autorizada e colocada na fila duravel. Nenhuma alteracao foi feita no Moodle nesta chamada; o status sera atualizado conforme cada item for processado." + batchHint;
        }

        if (response.Status == "retry_scheduled")
        {
            return $"A publicacao teve uma nao aplicacao confirmada por leitura do Moodle e foi agendada para retry seguro em {response.NextAttemptAt:O}. A aprovacao original continua valida; nao solicite outra confirmacao.";
        }

        if (response.Status == "execution_unknown")
        {
            return "O resultado remoto da publicação é desconhecido. Reconcilie cada item usando moodle_reconcile_write(pendingActionId, gradingItemId, resolution='auto'). Só nota e feedback observados no destino marcam o item como aplicado; baseline, divergência ou leitura incompleta mantêm o item incerto. A tool não reenvia a escrita. Não crie outra prévia para o item incerto.";
        }

        var outcome = response.FailedItems == 0
            ? "concluido"
            : response.SentItems == 0
                ? "nao realizado"
                : "concluido parcialmente";
        var summary = $"Lancamento Moodle {outcome}: {response.SentItems} enviado(s), {response.FailedItems} falha(s).";
        var reasons = response.Failures
            .Select(failure => failure.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => message.Trim().TrimEnd('.'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var warnings = (response.Warnings ?? [])
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => message.Trim().TrimEnd('.'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (reasons.Length == 0 && warnings.Length == 0)
        {
            return AppendPublicationItems(summary, response.Items);
        }

        var parts = new List<string>();
        if (reasons.Length > 0)
        {
            parts.Add($"Motivo(s): {string.Join("; ", reasons.Take(2))}");
        }

        if (warnings.Length > 0)
        {
            parts.Add($"Aviso(s): {string.Join("; ", warnings.Take(2))}");
        }

        var suffix = reasons.Length > 2 || warnings.Length > 2
            ? " Ha outros detalhes no resultado estruturado."
            : string.Empty;
        return AppendPublicationItems($"{summary} {string.Join(". ", parts)}.{suffix}", response.Items);
    }

    private static string AppendPublicationItems(
        string summary,
        IReadOnlyList<GradingLaunchPublicationItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            return summary;
        }

        var builder = new StringBuilder(summary);
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("Resultado por aluno:");
        foreach (var item in items)
        {
            builder.AppendLine($"- {item.StudentName ?? item.StudentId}: {item.Status}, nota {(item.PublishedGrade?.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) ?? "n/d")}, feedback {(item.PublishedFeedback is null ? "n/d" : "aplicado")}.");
        }

        return builder.ToString().TrimEnd();
    }


    private static string BuildPrepareAiBatchNarration(AiGradingBatchPackageResult data)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Pacote IA — Lote {data.BatchJobId}");
        sb.AppendLine();
        sb.AppendLine($"**{data.ItemsOnPage} item(ns)** nesta pagina; **{data.EligibleItems}** ainda elegiveis para analise pela IA.");
        if (data.ItemsExcludedByStatus > 0)
        {
            sb.AppendLine($"**{data.ItemsExcludedByStatus} item(ns)** ja fora da fila da IA nao foram repetidos. Cobertura persistida: **{data.PreparedItems}/{data.ExpectedItems}**; faltantes: **{data.MissingItems}**.");
        }
        sb.AppendLine();

        foreach (var item in data.Items)
        {
            var resourceInfo = item.Resources?.Count > 0
                ? $"{item.Resources.Count} arquivo(s) original(is) via MCP Resource" +
                  (item.Resources.Any(resource => resource.ArtifactId is not null)
                      ? $"; artifactId(s): {string.Join(", ", item.Resources.Where(resource => resource.ArtifactId is not null).Select(resource => resource.ArtifactId))}"
                      : string.Empty)
                : "sem arquivo original disponível";
            var gradeInfo = item.GradingMode == "feedback_only"
                ? "modo: somente feedback (sem nota)"
                : item.MaxGrade > 0
                    ? $"nota maxima: {item.MaxGrade}"
                    : "nota maxima: nao confirmada (sugestao numerica bloqueada)";
            sb.AppendLine($"- **Aluno {item.StudentId}** (item {item.GradingItemId}): {resourceInfo}, {gradeInfo}");
        }

        sb.AppendLine();
        sb.AppendLine(data.Instructions);
        return sb.ToString();
    }

    private static string BuildSaveAiGradingNarration(SaveAiGradingBatchResult data)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Resultado — Salvar Correcoes IA");
        sb.AppendLine();
        sb.AppendLine($"- **Salvos:** {data.SavedItems}");
        sb.AppendLine($"- **Ignorados:** {data.SkippedItems}");
        sb.AppendLine($"- **Falhas:** {data.FailedItems}");
        sb.AppendLine();
        sb.AppendLine($"**Proximo passo:** {data.NextStep}");
        if (data.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Avisos:** " + string.Join("; ", data.Warnings.Take(3)));
        }
        return sb.ToString();
    }

    internal static string BuildCorrectionsCsv(IReadOnlyList<GradingCorrectionsCsvRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("nome;nota;feedback");
        foreach (var row in rows)
        {
            builder.Append(CsvField(row.Nome));
            builder.Append(';');
            builder.Append(row.Nota?.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty);
            builder.Append(';');
            builder.Append(CsvField(row.Feedback));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string CsvField(string? value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static bool TryParseOptionalPositiveId(
        string? value,
        string name,
        out long? parsed,
        out string? error)
    {
        parsed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0)
        {
            parsed = number;
            return true;
        }

        error = $"O {name} deve ser numerico e positivo.";
        return false;
    }



}
