using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.Auditing;
using Moodle.Application.MoodleApi;
using Moodle.Application.Registry;
using Moodle.Domain.Registry;
using Moodle.Application.Tools;
using Microsoft.Extensions.Logging;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Tools;

[McpServerToolType]
public sealed class MoodleUniversalTools(
    IMoodleFunctionCatalog functionCatalog,
    ISafeReadExecutor safeReadExecutor,
    IOperationRegistry operationRegistry,
    IPolicyEngine policyEngine,
    IMoodleBusinessFlowRegistry businessFlows,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleConnectionSelection connectionSelection,
    IMoodleRestClient restClient,
    ILogger<MoodleUniversalTools> logger,
    IMcpToolUsageTelemetry runtimeTelemetry)
{
    [McpServerTool(Name = "moodle_diagnose_connection", Title = "Diagnosticar Conexao Moodle",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<MoodleConnectionDiagnostic>))]
    [MoodleToolMetadata(
        Family = "discovery",
        Classification = "R6",
        Kind = "diagnostic",
        CanonicalOperation = "connector.diagnostics.connection",
        Structural = true,
        ExposureStatus = "Diagnostic",
        ExposureReason = "Diagnostico tecnico detalhado para suporte e validacao de conexao; nao e necessario na superficie cognitiva normal.",
        Evidence = "Implementacao MoodleUniversalTools.DiagnoseConnectionAsync; preservada em Full e callable por compatibilidade.")]
    [Description("Sempre valida ao vivo a conexao Moodle e descobre as funcoes Web Service habilitadas. O parametro forceRefresh e mantido por compatibilidade; este diagnostico nao usa cache.")]
    public async Task<CallToolResult> DiagnoseConnectionAsync(
        [Description("Alias opcional da conexao Moodle.")] string? moodleAlias = null,
        [Description("Mantido por compatibilidade. O diagnostico sempre consulta o Moodle ao vivo.")] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        connectionSelection.Alias = moodleAlias;
        var auditId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            EnsureSiteInfoDiscoveryIsAllowed();
            var connection = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
            async Task<(JsonElement SiteInfo, MoodleFunctionProfile Profile)> ReadLiveProfileAsync()
            {
                var siteInfo = await restClient.CallAsync(
                    connection,
                    "core_webservice_get_site_info",
                    new Dictionary<string, object?>(),
                    allowServiceToken: false,
                    cancellationToken);
                return (siteInfo, MoodleFunctionProfileParser.Parse(connection, siteInfo));
            }

            var liveProfile = await runtimeTelemetry.MeasureRuntimePhaseAsync("diagnostic_live", ReadLiveProfileAsync);
            var liveSiteInfo = liveProfile.SiteInfo;
            var profile = liveProfile.Profile;
            var flows = businessFlows.EvaluateAll(profile);
            var data = new MoodleConnectionDiagnostic(
                Healthy: true,
                RequestedAlias: MoodleConnectionAlias.Normalize(moodleAlias),
                profile.ConnectionAlias,
                profile.ConnectionId,
                BaseUrl: SanitizeBaseUrl(connection.BaseUrl),
                ConnectionFound: true,
                Active: true,
                UrlValid: true,
                CredentialsPresent: true,
                DecryptionSucceeded: true,
                TokenAvailable: true,
                HttpSucceeded: true,
                AuthenticationSucceeded: true,
                SiteInfoSucceeded: true,
                profile.SiteName,
                profile.Release,
                profile.MoodleUserId,
                LatencyMs: stopwatch.ElapsedMilliseconds,
                profile.Functions.Count,
                profile.Functions.Count(function => function.Risk == MoodleFunctionRisk.Read),
                profile.Functions.Count(function => function.Risk == MoodleFunctionRisk.ControlledWrite),
                connection.CanWrite,
                flows,
                profile.DiscoveredAt,
                DiagnosticErrorCode: null,
                DiagnosticMessage: null);
            try
            {
                logger.LogInformation(
                    "Moodle diagnostic completed. AuditId={AuditId} Function={Function} HttpStatus={HttpStatus} DurationMs={DurationMs}",
                    auditId,
                    "core_webservice_get_site_info",
                    200,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception) { }
            return Success(
                data,
                $"Conexao '{profile.ConnectionAlias}' diagnosticada: {profile.Functions.Count} funcoes descobertas.",
                auditId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var descriptor = MoodleErrorContract.Describe(ex);
            var moodleFailure = ex as MoodleApiException;
            var stage = moodleFailure?.Stage ?? MoodleIntegrationStage.Unknown;
            var found = stage > MoodleIntegrationStage.ConnectionLookup ||
                descriptor.ErrorCode == MoodleErrorContract.ConnectionDisabled;
            var active = stage > MoodleIntegrationStage.ConnectionState;
            var urlValid = stage > MoodleIntegrationStage.UrlValidation;
            var credentialsPresent = stage > MoodleIntegrationStage.CredentialPresence;
            var decryptionSucceeded = stage > MoodleIntegrationStage.CredentialDecryption;
            var tokenAvailable = stage > MoodleIntegrationStage.TokenRequest &&
                descriptor.ErrorCode != MoodleErrorContract.AuthenticationFailed;
            var httpSucceeded = moodleFailure?.HttpStatusCode is >= 200 and < 300;
            var data = new MoodleConnectionDiagnostic(
                Healthy: false,
                RequestedAlias: MoodleConnectionAlias.Normalize(moodleAlias),
                Alias: moodleFailure?.ConnectionAlias ?? MoodleConnectionAlias.NormalizeOrDefault(moodleAlias),
                ConnectionId: moodleFailure?.ConnectionId,
                BaseUrl: SanitizeBaseUrl(moodleFailure?.Endpoint),
                ConnectionFound: found,
                Active: active,
                UrlValid: urlValid,
                CredentialsPresent: credentialsPresent,
                DecryptionSucceeded: decryptionSucceeded,
                TokenAvailable: tokenAvailable,
                HttpSucceeded: httpSucceeded,
                AuthenticationSucceeded: tokenAvailable &&
                    descriptor.ErrorCode != MoodleErrorContract.AuthenticationFailed,
                SiteInfoSucceeded: false,
                SiteName: null,
                Release: null,
                MoodleUserId: null,
                LatencyMs: stopwatch.ElapsedMilliseconds,
                FunctionCount: 0,
                ReadFunctionCount: 0,
                ControlledWriteFunctionCount: 0,
                CanWrite: false,
                Flows: [],
                DiscoveredAt: DateTimeOffset.UtcNow,
                DiagnosticErrorCode: descriptor.ErrorCode,
                DiagnosticMessage: descriptor.Message);
            try
            {
                logger.LogWarning(
                    "Moodle diagnostic failed. AuditId={AuditId} ErrorCode={ErrorCode} HttpStatus={HttpStatus} DurationMs={DurationMs}",
                    descriptor.AuditId,
                    descriptor.ErrorCode,
                    moodleFailure?.HttpStatusCode,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception) { }
            return Success(data, descriptor.Message, descriptor.AuditId, [descriptor.Message]);
        }
    }

    [McpServerTool(Name = "moodle_list_functions", Title = "Listar Funcoes Moodle",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<IReadOnlyList<MoodleFunctionDescriptor>>))]
    [MoodleToolMetadata(
        Family = "discovery",
        Classification = "R6",
        Kind = "diagnostic",
        CanonicalOperation = "connector.diagnostics.functions",
        Structural = true,
        ExposureStatus = "Diagnostic",
        ExposureReason = "Lista tecnica de funcoes Moodle para suporte e troubleshooting; fluxos de negocio usam capabilities internamente.",
        Evidence = "Implementacao MoodleUniversalTools.ListFunctionsAsync; preservada em Full e callable por compatibilidade.")]
    [Description("Lista todas as funcoes Web Service habilitadas para o token da conexao Moodle atual. Funcoes de consulta executam diretamente; as demais usam a confirmacao de escrita.")]
    public async Task<CallToolResult> ListFunctionsAsync(
        [Description("Termo opcional para filtrar o nome da funcao.")] string? search = null,
        [Description("Alias opcional da conexao Moodle.")] string? moodleAlias = null,
        [Description("Ignora o cache e consulta novamente o Moodle.")] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        connectionSelection.Alias = moodleAlias;
        try
        {
            EnsureSiteInfoDiscoveryIsAllowed();
            var profile = await functionCatalog.GetCurrentAsync(forceRefresh, cancellationToken);
            var functions = string.IsNullOrWhiteSpace(search)
                ? profile.Functions
                : profile.Functions.Where(function => function.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            functions = functions.Where(function => function.IsAvailable).ToArray();
            return Success<IReadOnlyList<MoodleFunctionDescriptor>>(
                functions,
                $"{functions.Count} funcao(oes) encontrada(s).",
                freshness: BuildCapabilitiesFreshness(profile, functions.Count, "moodle_functions"));
        }
        catch (OperationCanceledException) { throw; }
        catch (MoodleApiException ex) { return ToolResultHelper.Error<IReadOnlyList<MoodleFunctionDescriptor>>(ex); }
        catch (InvalidOperationException ex) { return ToolResultHelper.Error<IReadOnlyList<MoodleFunctionDescriptor>>(ex.Message); }
    }

    [McpServerTool(Name = "moodle_check_function", Title = "Verificar Funcao Moodle",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<MoodleFunctionDescriptor>))]
    [MoodleToolMetadata(
        Family = "discovery",
        Classification = "R6",
        Kind = "diagnostic",
        CanonicalOperation = "connector.diagnostics.function",
        Structural = true,
        ExposureStatus = "Diagnostic",
        ExposureReason = "Verificacao tecnica de uma funcao remota para suporte; nao representa uma intencao academica distinta.",
        Evidence = "Implementacao MoodleUniversalTools.CheckFunctionAsync; preservada em Full e callable por compatibilidade.")]
    [Description("Confirma se uma funcao Moodle esta disponivel para o token atual e informa se ela executa como consulta ou exige confirmacao de escrita.")]
    public async Task<CallToolResult> CheckFunctionAsync(
        [Description("Nome exato da funcao Web Service Moodle.")] string functionName,
        [Description("Alias opcional da conexao Moodle.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return ToolResultHelper.Error<MoodleFunctionDescriptor>("Informe o nome da funcao Moodle.");
        }

        connectionSelection.Alias = moodleAlias;
        try
        {
            EnsureSiteInfoDiscoveryIsAllowed();
            var profile = await functionCatalog.GetCurrentAsync(false, cancellationToken);
            var discovered = profile.Functions.FirstOrDefault(function =>
                string.Equals(function.Name, functionName.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? new MoodleFunctionDescriptor(functionName.Trim(), MoodleFunctionRisk.Unknown, false);
            var descriptor = discovered;
            return Success(descriptor, descriptor.IsAvailable ? "Funcao Moodle disponivel." : "Funcao Moodle nao esta disponivel para esta conexao.");
        }
        catch (OperationCanceledException) { throw; }
        catch (MoodleApiException ex) { return ToolResultHelper.Error<MoodleFunctionDescriptor>(ex); }
        catch (InvalidOperationException ex) { return ToolResultHelper.Error<MoodleFunctionDescriptor>(ex.Message); }
    }

    [McpServerTool(Name = "moodle_list_available_flows", Title = "Listar Fluxos Moodle Disponiveis",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<IReadOnlyCollection<BusinessFlowAvailability>>))]
    [MoodleToolMetadata(
        Family = "discovery",
        Classification = "R6",
        Kind = "capability-discovery",
        CanonicalOperation = "connector.capabilities.flows",
        Structural = true,
        ExposureStatus = "Keep",
        ExposureReason = "Descoberta de fluxos disponiveis orienta clientes sem descoberta dinamica e preserva fallback explicito.",
        Evidence = "Referenciado pelo ADR-0001 e pelas skills de cursos/core; permanece exposto em Production.")]
    [Description("Avalia os fluxos acadêmicos registrados para a conexão Moodle atual, selecionando a melhor estratégia ou informando as funções ausentes.")]
    public async Task<CallToolResult> ListAvailableFlowsAsync(
        [Description("Alias opcional da conexão Moodle.")] string? moodleAlias = null,
        [Description("Ignora o cache e consulta novamente o Moodle.")] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        connectionSelection.Alias = moodleAlias;
        try
        {
            EnsureSiteInfoDiscoveryIsAllowed();
            var profile = await functionCatalog.GetCurrentAsync(forceRefresh, cancellationToken);
            var flows = businessFlows.EvaluateAll(profile);
            return Success<IReadOnlyCollection<BusinessFlowAvailability>>(
                flows,
                $"{flows.Count(flow => flow.IsAvailable)} fluxo(s) Moodle disponível(is).",
                freshness: BuildCapabilitiesFreshness(profile, flows.Count, "available_flows"));
        }
        catch (OperationCanceledException) { throw; }
        catch (MoodleApiException ex) { return ToolResultHelper.Error<IReadOnlyCollection<BusinessFlowAvailability>>(ex); }
        catch (InvalidOperationException ex) { return ToolResultHelper.Error<IReadOnlyCollection<BusinessFlowAvailability>>(ex.Message); }
    }

    [McpServerTool(Name = "moodle_execute_read", Title = "Executar Leitura Moodle",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<MoodleFunctionResult>))]
    [MoodleToolMetadata(
        Family = "discovery",
        Classification = "R6",
        Kind = "read",
        CanonicalOperation = "moodle.execute_read",
        Structural = true,
        ExposureStatus = "Keep",
        ExposureReason = "Executa somente funções classificadas como leitura segura pelo catálogo Moodle; funções de escrita seguem preparação e confirmação.")]
    [Description("Executa uma funcao Moodle de consulta habilitada para o token da conexao atual. Qualquer funcao que possa alterar estado, inclusive remocao, e redirecionada para moodle_prepare_write e confirmacao literal.")]
    public async Task<CallToolResult> ExecuteReadAsync(
        [Description("Nome exato da funcao Web Service Moodle.")] string functionName,
        [Description("Objeto JSON com os parametros da funcao Moodle.")] JsonElement parameters,
        [Description("Alias opcional da conexao Moodle.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return ToolResultHelper.Error<MoodleFunctionResult>("Informe o nome da funcao Moodle.");
        }

        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return ToolResultHelper.Error<MoodleFunctionResult>("Os parametros devem ser fornecidos como um objeto JSON.");
        }

        connectionSelection.Alias = moodleAlias;
        try
        {
            var values = parameters.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
            var normalized = await safeReadExecutor.ExecuteAsync(
                functionName,
                values,
                moodleAlias,
                new NormalizationContext(NormalizationMode.Agent),
                cancellationToken);
            // Universal reads are intentionally open-ended. The normalizer is
            // not a security boundary, so redact the final public payload as
            // well as audit data before it reaches MCP/ChatGPT.
            var projection = MoodlePublicContentSanitizer.Inspect(normalized);
            var data = new MoodleFunctionResult(functionName.Trim(), projection.Payload);
            return Success(data, $"Funcao de leitura '{data.Function}' executada com sucesso.",
                warnings: projection.Complete ? [] : ["Cobertura parcial: parte do conteúdo foi retida por exceder os limites de inspeção pública."]);
        }
        catch (OperationCanceledException) { throw; }
        catch (MoodleApiException ex) { return ToolResultHelper.Error<MoodleFunctionResult>(ex); }
        catch (ArgumentException ex) { return ToolResultHelper.Error<MoodleFunctionResult>(ex.Message); }
        catch (InvalidOperationException ex)
        {
            var errorCode = ex.Message.Contains("not registered", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                ? MoodleErrorContract.UnknownMoodleFunction
                : null;
            return ToolResultHelper.Error<MoodleFunctionResult>(ex.Message, errorCode: errorCode);
        }
    }

    private static CallToolResult Success<T>(
        T data,
        string narration,
        string? auditId = null,
        IReadOnlyList<string>? warnings = null,
        ToolFreshness? freshness = null)
    {
        var response = new ToolResponse<T>(
            "ok",
            data,
            warnings ?? [],
            AuditId: auditId ?? Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            Message: narration,
            Freshness: freshness);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = narration }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }

    private static ToolFreshness BuildCapabilitiesFreshness(
        MoodleFunctionProfile profile,
        int recordCount,
        string recordType) =>
        new(
            profile.IsCached ? "cache" : "live",
            profile.DiscoveredAt,
            Math.Max(0, (long)(DateTimeOffset.UtcNow - profile.DiscoveredAt).TotalSeconds),
            Stale: false,
            RefreshQueued: false,
            Complete: true,
            RecordCount: recordCount,
            DecisionSafe: true,
            Dataset: "capabilities",
            RecordType: recordType);

    private void EnsureSiteInfoDiscoveryIsAllowed()
    {
        var operation = operationRegistry.GetOperation("core_webservice_get_site_info");
        var decision = policyEngine.Evaluate(operation);
        if (decision.Decision != PolicyDecision.Allow)
        {
            throw new InvalidOperationException(
                $"Policy Denied: discovery requires the registered read operation 'core_webservice_get_site_info'. {decision.Reason}");
        }
    }

    private static string? SanitizeBaseUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        }.Uri.AbsoluteUri.TrimEnd('/');
    }

}

public sealed record MoodleConnectionDiagnostic(
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("requestedAlias")] string? RequestedAlias,
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("connectionId")] string? ConnectionId,
    [property: JsonPropertyName("baseUrl")] string? BaseUrl,
    [property: JsonPropertyName("connectionFound")] bool ConnectionFound,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("urlValid")] bool UrlValid,
    [property: JsonPropertyName("credentialsPresent")] bool CredentialsPresent,
    [property: JsonPropertyName("decryptionSucceeded")] bool DecryptionSucceeded,
    [property: JsonPropertyName("tokenAvailable")] bool TokenAvailable,
    [property: JsonPropertyName("httpSucceeded")] bool HttpSucceeded,
    [property: JsonPropertyName("authenticationSucceeded")] bool AuthenticationSucceeded,
    [property: JsonPropertyName("siteInfoSucceeded")] bool SiteInfoSucceeded,
    [property: JsonPropertyName("siteName")] string? SiteName,
    [property: JsonPropertyName("release")] string? Release,
    [property: JsonPropertyName("moodleUserId")] long? MoodleUserId,
    [property: JsonPropertyName("latencyMs")] long LatencyMs,
    [property: JsonPropertyName("functionCount")] int FunctionCount,
    [property: JsonPropertyName("readFunctionCount")] int ReadFunctionCount,
    [property: JsonPropertyName("controlledWriteFunctionCount")] int ControlledWriteFunctionCount,
    [property: JsonPropertyName("canWrite")] bool CanWrite,
    [property: JsonPropertyName("flows")] IReadOnlyCollection<BusinessFlowAvailability> Flows,
    [property: JsonPropertyName("discoveredAt")] DateTimeOffset DiscoveredAt,
    [property: JsonPropertyName("diagnosticErrorCode")] string? DiagnosticErrorCode,
    [property: JsonPropertyName("diagnosticMessage")] string? DiagnosticMessage);
