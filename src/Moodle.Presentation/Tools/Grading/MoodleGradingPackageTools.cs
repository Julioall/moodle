using System.ComponentModel;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Grading;
using Moodle.Application.Grading.Packages;
using Moodle.Application.Tools;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Tools;

namespace Moodle.Presentation.Tools.Grading;

[McpServerToolType]
public sealed class MoodleGradingPackageTools(IMediator mediator)
{
    [MoodleToolMetadata(
        Family = "assignments",
        Classification = "R3",
        Kind = "wrapper",
        CanonicalOperation = "grading.compare_selected_model_answer",
        ExposureStatus = "Keep",
        ExposureReason = "Per-request exact binary comparison selected explicitly by a teacher.",
        Evidence = "Requires a submission resource and an assignment-context attachment from the same activity; returns only an exact-match signal and private resource references.",
        RequiredPlatformPermission = "tool.assignments.grade")]
    [McpServerTool(
        Name = "compare_submission_to_selected_model",
        Title = "Compare Submission to Selected Model Answer",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<CompareSelectedModelAnswerResult>))]
    [Description("Compara exatamente os bytes de um arquivo de submissao com um anexo oficial de contexto da mesma atividade, selecionado explicitamente nesta requisicao. Informe o ID Moodle da atividade (assignment instance, nao cmid) e as URIs opacas dos resources. A resposta retorna apenas igualdade e referencias privadas; nao expoe hash ou conteudo, nao salva resultado da comparacao, nao atribui nota e nao conclui fraude. A leitura pode registrar o hash de integridade do resource pelo fluxo padrao.")]
    public async Task<CallToolResult> CompararSubmissaoComModeloSelecionadoAsync(
        [Description("ID numerico do curso no Moodle.")]
        string courseId,
        [Description("ID numerico da atividade mod_assign, nao o cmid.")]
        string assignmentId,
        [Description("URI moodle://resource/{id} do arquivo de submissao selecionado.")]
        string submissionResourceUri,
        [Description("URI moodle://resource/{id} do anexo oficial de contexto explicitamente selecionado como modelo somente para esta chamada.")]
        string modelResourceUri,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await mediator.Send(
                new CompareSelectedModelAnswerQuery(
                    courseId,
                    assignmentId,
                    submissionResourceUri,
                    modelResourceUri),
                cancellationToken);
            var response = new ToolResponse<CompareSelectedModelAnswerResult>(
                "ok",
                data,
                [],
                AuditId: null,
                DateTimeOffset.UtcNow);
            return new CallToolResult
            {
                Content = [new TextContentBlock
                {
                    Text = data.Identical
                        ? "Os bytes dos resources selecionados sao identicos. Este sinal nao e uma avaliacao academica ou disciplinar."
                        : "Os bytes dos resources selecionados sao diferentes. Nenhuma conclusao academica ou disciplinar foi produzida."
                }],
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
            return ToolResultHelper.Error<CompareSelectedModelAnswerResult>(
                exception,
                errorPhase: "acquisition",
                nextAction: "review_resource_scope_and_access");
        }
        catch (ArgumentException exception)
        {
            return ToolResultHelper.Error<CompareSelectedModelAnswerResult>(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ToolResultHelper.Error<CompareSelectedModelAnswerResult>(
                exception,
                errorPhase: "acquisition",
                nextAction: "review_resource_scope_and_retry");
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<CompareSelectedModelAnswerResult>(
                exception,
                errorPhase: "acquisition",
                nextAction: "review_resource_access_and_retry");
        }
    }

    [McpServerTool(
        Name = "prepare_grading_packages",
        Title = "Prepare Grading Evidence Packages",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GradingPackagePreparationResult>))]
    [Description("Cria ou reaproveita ZIPs por atividade/contexto, reunindo entregas entre sublotes do gradingRunId. Prefira esta ferramenta uma vez por gradingRunId quando houver mais de uma correcao, em vez de ler resources individuais em serie. Use packageMode=per_activity (padrao); packageMode=per_submission cria um pacote por entrega. Divide pelos limites de transporte e retorna activityGroups como indice geral das partes e itens. gradingItemIds seleciona retentativas especificas. Preserva pacotes e rascunhos anteriores. Enfileira em background por padrao; waitForCompletion=true permite espera curta. Consulte get_grading_package_status para pacotes ainda na fila e leia os ZIPs prontos por resources/read ou read_grading_package. Use read_moodle_resource somente para itens sem cobertura; se um ZIP inteiro estiver indisponivel, incompleto, invalido ou acima do limite seguro, limite o fallback aos itens daquele pacote.")]
    public async Task<CallToolResult> PrepararPacotesCorrecaoAsync(
        [Description("batchJobId ou gradingRunId retornado por start_pending_grading_run.")]
        Guid batchJobId,
        [Description("Quando true, regenera somente o transporte ZIP de itens que ja possuem rascunho, sem reabrir ou sobrescrever a correcao existente.")]
        bool regenerateTransport = false,
        [Description("Quando true, aguarda de forma limitada os pacotes chegarem a um estado terminal e devolve os descritores atualizados. O padrao false preserva o retorno rapido e a fila em background.")]
        bool waitForCompletion = false,
        [Description("Tempo maximo da espera opcional, em segundos. O valor e limitado entre 1 e 60 segundos.")]
        int waitTimeoutSeconds = 30,
        [Description("per_activity (padrao) agrupa entregas da mesma atividade/contexto entre os sublotes do gradingRunId, com partes limitadas por tamanho. per_submission cria um pacote por entrega. activityGroups relaciona todas as partes e os IDs selecionados.")]
        string packageMode = "per_activity",
        [Description("Opcional: IDs de itens do escopo solicitado para preparar ou retentar somente essas entregas. Omita para selecionar todas as pendentes. Rascunhos so entram quando regenerateTransport=true.")]
        Guid[]? gradingItemIds = null,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty)
        {
            return ToolResultHelper.Error<GradingPackagePreparationResult>("Informe um identificador de lote valido.");
        }

        if (packageMode is not ("per_activity" or "per_submission"))
        {
            return ToolResultHelper.Error<GradingPackagePreparationResult>("packageMode deve ser per_activity ou per_submission.");
        }

        try
        {
            var data = await mediator.Send(
                new PrepareGradingPackagesCommand(batchJobId, regenerateTransport, packageMode, gradingItemIds),
                cancellationToken);
            if (waitForCompletion)
            {
                data = await WaitForCompletionAsync(
                    data,
                    TimeSpan.FromSeconds(Math.Clamp(waitTimeoutSeconds, 1, 60)),
                    cancellationToken);
            }
            var response = new ToolResponse<GradingPackagePreparationResult>(
                "ok",
                data,
                data.Warnings,
                AuditId: null,
                DateTimeOffset.UtcNow);
            return new CallToolResult
            {
                Content = [new TextContentBlock
                {
                    Text = waitForCompletion
                        ? $"Preparacao de pacotes concluida ou aguardando limite: {data.Packages.Count} pacote(s), {data.PendingItems.Count} item(ns) selecionado(s). Consulte get_grading_package_status para acompanhar o que permanecer em fila."
                        : $"Preparacao de pacotes enfileirada: {data.Packages.Count} pacote(s), {data.PendingItems.Count} item(ns) selecionado(s). Consulte get_grading_package_status para acompanhar."
                }],
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
            return ToolResultHelper.Error<GradingPackagePreparationResult>(exception, errorPhase: "package", nextAction: "review_access_and_check_package_status");
        }
        catch (InvalidOperationException exception)
        {
            return ToolResultHelper.Error<GradingPackagePreparationResult>(exception, errorPhase: "package", nextAction: "check_package_status_before_retry");
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<GradingPackagePreparationResult>(exception, errorPhase: "package", nextAction: "check_package_status_before_retry");
        }
    }

    private async Task<GradingPackagePreparationResult> WaitForCompletionAsync(
        GradingPackagePreparationResult initial,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (initial.Packages.Count == 0 || initial.Packages.All(IsTerminal))
        {
            return initial;
        }

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var latest = initial.Packages.ToDictionary(package => package.PackageId);
        var warnings = initial.Warnings.ToList();

        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var packageId in latest.Keys.ToArray())
            {
                try
                {
                    var status = await mediator.Send(
                        new GetGradingPackageStatusQuery(packageId),
                        cancellationToken);
                    latest[packageId] = status.Descriptor;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    warnings.Add("Nao foi possivel acompanhar um pacote durante a espera opcional; a preparacao continua em background e os identificadores originais foram preservados.");
                    return initial with
                    {
                        Packages = initial.Packages
                            .Select(package => latest.TryGetValue(package.PackageId, out var current) ? current : package)
                            .ToArray(),
                        Warnings = warnings
                    };
                }
            }

            if (latest.Values.All(IsTerminal))
            {
                return initial with
                {
                    Packages = initial.Packages
                        .Select(package => latest.TryGetValue(package.PackageId, out var current) ? current : package)
                        .ToArray(),
                    Warnings = warnings
                };
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(500)
                    ? remaining
                    : TimeSpan.FromMilliseconds(500),
                cancellationToken);
        }

        warnings.Add("A preparacao continua em background; o tempo de espera opcional terminou. Consulte get_grading_package_status para acompanhar os pacotes pendentes.");
        return initial with
        {
            Packages = initial.Packages
                .Select(package => latest.TryGetValue(package.PackageId, out var current) ? current : package)
                .ToArray(),
            Warnings = warnings
        };
    }

    private static bool IsTerminal(GradingPackageDescriptor descriptor) =>
        descriptor.Status is "Ready" or "PartiallyReady" or "NeedsAttention" or "ResultsReceived" or "Cancelled";

    [McpServerTool(
        Name = "get_grading_package_status",
        Title = "Get Grading Evidence Package Status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GradingPackageStatusResult>))]
    [Description("Consulta o status de um pacote de evidencias, devolve manifesto/instrucoes legiveis, hash, cobertura, expiracao, erros de preparacao e a referencia ZIP no structuredContent quando disponivel. A referencia nao e anexada automaticamente ao content da tool; leia o ZIP explicitamente com resources/read, read_grading_package ou read_moodle_resource.")]
    public async Task<CallToolResult> ConsultarStatusPacoteCorrecaoAsync(
        [Description("Identificador do pacote retornado por prepare_grading_packages.")]
        Guid packageId,
        CancellationToken cancellationToken = default)
    {
        if (packageId == Guid.Empty)
        {
            return ToolResultHelper.Error<GradingPackageStatusResult>("Informe um packageId valido.");
        }

        try
        {
            var data = await mediator.Send(new GetGradingPackageStatusQuery(packageId), cancellationToken);
            var response = new ToolResponse<GradingPackageStatusResult>(
                "ok",
                data,
                data.Warnings,
                AuditId: null,
                DateTimeOffset.UtcNow);
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = $"Pacote {data.Descriptor.PackageId}: {data.Descriptor.Status}, cobertura {data.Descriptor.IncludedItems}/{data.Descriptor.SelectedItems}; proximo passo: {data.Descriptor.NextStep}"
                    }
                ],
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
            return ToolResultHelper.Error<GradingPackageStatusResult>(exception, errorPhase: "query", nextAction: "review_access_and_retry_status_read");
        }
        catch (InvalidOperationException exception)
        {
            return ToolResultHelper.Error<GradingPackageStatusResult>(exception, errorPhase: "query", nextAction: "retry_status_read");
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<GradingPackageStatusResult>(exception, errorPhase: "query", nextAction: "retry_status_read");
        }
    }

    [McpServerTool(
        Name = "import_grading_package_results",
        Title = "Import Grading Package Results",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<GradingPackageImportResult>))]
    [Description("Valida e importa resultados JSON por gradingItemId como rascunhos locais. A importacao e parcial por item, devolve complete/pending para distinguir cobertura ausente de needs_review, rejeita hash/contexto/escala/evidencia inconsistentes, trata repeticao identica como unchanged e nunca publica no Moodle.")]
    public async Task<CallToolResult> ImportarResultadosPacoteCorrecaoAsync(
        [Description("Identificador do pacote.")]
        Guid packageId,
        [Description("SHA-256 do manifest.json que foi lido pelo modelo.")]
        string manifestHash,
        [Description("Documento JSON conforme formato-resposta.schema.json dentro do pacote.")]
        string resultsJson,
        CancellationToken cancellationToken = default)
    {
        if (packageId == Guid.Empty || string.IsNullOrWhiteSpace(manifestHash) || string.IsNullOrWhiteSpace(resultsJson))
        {
            return ToolResultHelper.Error<GradingPackageImportResult>("Informe packageId, manifestHash e resultsJson.");
        }

        try
        {
            var data = await mediator.Send(
                new ImportGradingPackageResultsCommand(packageId, manifestHash, resultsJson),
                cancellationToken);
            var responseStatus = data.Rejected > 0 || !data.Complete
                ? "partial_failure"
                : data.Pending > 0
                    ? "ok_with_review"
                    : "ok";
            var response = new ToolResponse<GradingPackageImportResult>(
                responseStatus,
                data,
                data.Errors.Select(error => error.Message).ToArray(),
                AuditId: null,
                DateTimeOffset.UtcNow);
            return new CallToolResult
            {
                Content = [new TextContentBlock
                {
                    Text = $"Resultados do pacote importados: {data.Saved} salvo(s), {data.Unchanged} inalterado(s), {data.Pending} pendente(s), {data.Rejected} rejeitado(s). Revise os rascunhos antes da previa de publicacao."
                }],
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
            return ToolResultHelper.Error<GradingPackageImportResult>(exception, errorPhase: "save", nextAction: "review_access_and_check_grading_status");
        }
        catch (InvalidOperationException exception)
        {
            return ToolResultHelper.Error<GradingPackageImportResult>(exception, errorPhase: "save", nextAction: "check_grading_status_before_retry");
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<GradingPackageImportResult>(exception, errorPhase: "save", nextAction: "check_grading_status_before_retry");
        }
    }
}
