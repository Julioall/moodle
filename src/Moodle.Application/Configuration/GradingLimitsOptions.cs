namespace Moodle.Application.Configuration;

public sealed class GradingLimitsOptions
{
    public const string SectionName = "GradingLimits";

    public int MaxBatchItems { get; init; } = 400;

    public int MaxFileSizeMb { get; init; } = 25;

    /// <summary>
    /// Limite específico para a entrega de pacotes SCORM como resources ZIP.
    /// Não amplia os limites de downloads e submissões comuns.
    /// </summary>
    public int MaxScormPackageResourceBytes { get; init; } = 100 * 1024 * 1024;

    /// <summary>Maximum binary size returned by a single MCP resource read.</summary>
    public int MaxResourceBytes { get; init; } = 25 * 1024 * 1024;

    /// <summary>
    /// Mantém os recursos das entregas disponíveis enquanto uma correção
    /// grande é revisada. A expiração do recurso é independente da expiração
    /// da ação de publicação.
    /// </summary>
    public int ResourceExpirationMinutes { get; init; } = 24 * 60;

    public int MaxConcurrentResourceDownloads { get; init; } = 4;

    public int MaxZipEntries { get; init; } = 100;

    public long MaxExtractedZipBytes { get; init; } = 100 * 1024 * 1024;

    public int MaxFilesPerSubmission { get; init; } = 10;

    public int MaxTextCharsPerSubmission { get; init; } = 120_000;

    public int MaxReviewItemsPerPage { get; init; } = 25;

    /// <summary>
    /// Initial operational recommendation for the number of grading items
    /// grouped into one publication preview. This is a homol hypothesis, not
    /// a limit imposed by Moodle.
    /// </summary>
    public int PublicationRecommendedMaxItems { get; init; } = 20;

    /// <summary>
    /// Maximum number of approved items whose mutable Moodle state is read
    /// together before the worker starts the next write block.
    /// </summary>
    public int PublicationPreflightBatchSize { get; init; } = 5;

    /// <summary>
    /// Habilita a chamada mod_assign_save_grades para grupos pequenos. Deve
    /// permanecer desligada até a homologação do Moodle confirmar o payload
    /// e a reconciliação individual de cada item.
    /// </summary>
    public bool EnableBatchGradeWrite { get; init; }

    /// <summary>Quantidade máxima de alunos por chamada de escrita coletiva.</summary>
    public int PublicationWriteBatchSize { get; init; } = 5;

    /// <summary>
    /// Fresh preflight reads older than this are discarded before a write.
    /// </summary>
    public int PublicationPreflightMaxAgeSeconds { get; init; } = 120;

    public int RawFileRetentionDays { get; init; } = 7;

    public int DraftRetentionDays { get; init; } = 180;

    public int MoodleMaxConcurrentRequests { get; init; } = 5;

    /// <summary>
    /// Limite legado por item. Os limites explícitos abaixo também protegem
    /// o lote e a conexão; manter os três permite uma migração de configuração
    /// sem aumentar a concorrência acidentalmente.
    /// </summary>
    public int FileDownloadWorkers { get; init; } = 4;

    public int MaxConcurrentDownloadsPerConnection { get; init; } = 4;

    public int MaxConcurrentDownloadsPerBatch { get; init; } = 4;

    public int ExtractionWorkers { get; init; } = 4;

    public int AiAnalysisWorkers { get; init; } = 3;

    /// <summary>
    /// Migração gradual: quando habilitado, o request cria somente o job e
    /// referências técnicas; downloads/extração/contexto ficam para o worker.
    /// </summary>
    public bool DeferHeavyIngestion { get; init; }

    public int BatchLeaseMinutes { get; init; } = 30;

    /// <summary>
    /// Intervalo mínimo antes de repetir uma leitura externa que falhou de
    /// forma transitória. O item permanece pendente e reaproveita o contexto
    /// já persistido quando voltar à fila.
    /// </summary>
    public int TransientProcessingRetryDelaySeconds { get; init; } = 30;

    public int DurableBatchPollSeconds { get; init; } = 5;

    public int DurableBatchClaimSize { get; init; } = 8;

    /// <summary>
    /// Concorrência inicial do pool adaptativo. O pool pode subir ou descer
    /// entre BatchWorkerMinConcurrency e BatchWorkerMaxConcurrency.
    /// </summary>
    public int BatchWorkerConcurrency { get; init; } = 2;

    /// <summary>Ativa o ajuste automático conforme fila, CPU e memória.</summary>
    public bool AdaptiveBatchWorkerConcurrency { get; init; } = true;

    /// <summary>Menor número de slots mantidos pelo pool quando há demanda.</summary>
    public int BatchWorkerMinConcurrency { get; init; } = 1;

    /// <summary>
    /// Teto explícito do pool. Zero calcula o teto a partir de CPU e memória
    /// disponíveis no processo/container (com limite absoluto de 32).
    /// </summary>
    public int BatchWorkerMaxConcurrency { get; init; }

    /// <summary>Utilização alvo de CPU do processo, normalmente 80%.</summary>
    public int BatchWorkerCpuTargetPercent { get; init; } = 80;

    /// <summary>Utilização alvo de memória disponível, normalmente 80%.</summary>
    public int BatchWorkerMemoryTargetPercent { get; init; } = 80;

    /// <summary>Intervalo entre decisões de escala do pool.</summary>
    public int BatchWorkerScaleIntervalSeconds { get; init; } = 5;

    /// <summary>Tempo sem fila antes de reduzir slots até o mínimo.</summary>
    public int BatchWorkerIdleScaleDownSeconds { get; init; } = 30;

    /// <summary>
    /// Reserva conservadora de memória por lote para calcular o teto automático.
    /// </summary>
    public int BatchWorkerMemoryPerWorkerMb { get; init; } = 256;

    /// <summary>
    /// Evita que vários lotes do mesmo Moodle saturem a API enquanto permite
    /// que conexões independentes avancem em paralelo.
    /// </summary>
    public int BatchWorkerPerConnectionConcurrency { get; init; } = 2;

    /// <summary>
    /// Frequência do worker que retoma publicações autorizadas após queda ou
    /// timeout do request de confirmação.
    /// </summary>
    public int PublicationWorkerPollSeconds { get; init; } = 5;

    /// <summary>Publicações que uma instância pode tentar retomar em paralelo.</summary>
    public int PublicationWorkerConcurrency { get; init; } = 2;

    /// <summary>
    /// Limita escritas de publicação simultâneas contra a mesma instalação
    /// Moodle dentro de uma instância do conector.
    /// </summary>
    public int PublicationWorkerPerConnectionConcurrency { get; init; } = 1;

    /// <summary>
    /// Janela legada de fallback para revisão da publicação.
    /// O fluxo principal usa Moodle:StablePendingActionExpirationHours.
    /// </summary>
    public int PublicationReviewExpirationHours { get; init; } = 168;

    /// <summary>
    /// Orçamento total da chamada MCP de criação. Deve ficar abaixo do timeout
    /// do transporte para devolver um erro estruturado, não um HTTP 504.
    /// </summary>
    public int BatchCreationTimeoutSeconds { get; init; } = 75;

    /// <summary>
    /// Reservada para um rollout futuro de seleção automática. As tools
    /// explícitas de pacotes permanecem registradas e nunca bloqueiam o
    /// fallback individual quando esta opção está desligada.
    /// </summary>
    public bool GradingPackageEnabled { get; init; }

    public int MaxGradingPackageItems { get; init; } = 20;

    public int MaxGradingPackageZipMb { get; init; } = 20;

    public int MaxGradingPackageUncompressedMb { get; init; } = 80;

    public int MaxGradingPackageEntries { get; init; } = 80;

    public int GradingPackageDownloadConcurrency { get; init; } = 4;

    /// <summary>
    /// Diretório privado usado no desenvolvimento. Em produção com mais de
    /// uma réplica deve apontar para storage compartilhado ou a feature deve
    /// permanecer desligada.
    /// </summary>
    public string? GradingPackageBlobRootDirectory { get; init; }

    public int GradingPackageMaxRetries { get; init; } = 5;

    public int GradingPackageRetryBaseSeconds { get; init; } = 30;

    public int GradingPackagePollSeconds { get; init; } = 5;

    public int GradingPackageWorkerConcurrency { get; init; } = 1;

    /// <summary>
    /// Reuses only complete assignment-submission reads within one process.
    /// Keep disabled until a controlled rollout is approved.
    /// </summary>
    public bool GradingPackageSubmissionReuseEnabled { get; init; } = false;

    public int GradingPackageSubmissionReuseSeconds { get; init; } = 30;

    public long GradingPackageSubmissionCacheMaxBytes { get; init; } = 128L * 1024 * 1024;
}
