using Microsoft.Extensions.DependencyInjection;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Application.Memory;
using Moodle.Application.MoodleApi;
using Moodle.Application.PendingActions;
using Moodle.Application.Registry;
using Microsoft.Extensions.Options;

namespace Moodle.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
        services.AddOptions<AssignmentWriteFeatureOptions>();
        services.AddOptions<MessageWriteFeatureOptions>();
        services.AddOptions<MoodleUniversalApiFeatureOptions>();
        services.AddSingleton<IMoodleBusinessFlowRegistry, MoodleBusinessFlowRegistry>();
        services.AddSingleton<IOperationRegistry, OperationRegistry>();
        services.AddScoped<IConnectionRegistry, ConnectionRegistry>();
        services.AddScoped<ICapabilityRegistry, CapabilityRegistry>();
        services.AddSingleton<IPolicyEngine, PolicyEngine>();
        services.AddSingleton<IResponseNormalizer, ResponseNormalizer>();
        services.AddScoped<ISafeReadExecutor, SafeReadExecutor>();
        services.AddOptions<GradingLimitsOptions>();
        services.AddOptions<ReportTemplateOptions>()
            .Validate(options => options.IsValid(), "Invalid report template limits.")
            .ValidateOnStart();
        services.AddSingleton<Reports.Templates.ReportCatalogService>();
        services.AddSingleton<Reports.Templates.ReportDefinitionValidator>();
        services.AddScoped<Reports.Templates.ReportTemplateService>();
        services.AddScoped<Reports.Templates.ReportDatasetResolver>();
        services.AddSingleton<Reports.Templates.ReportPreviewGate>();
        services.AddScoped<Reports.Templates.ReportPreviewService>();
        services.AddScoped<Reports.Templates.ReportGenerationService>();
        services.AddScoped<IPendingActionService, PendingActionService>();
        services.AddScoped<IActionConfirmationService, ActionConfirmationService>();
        services.AddScoped<IUserMemoryService, UserMemoryService>();
        services.AddScoped<IUserMemoryDocumentService, UserMemoryDocumentService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IGradingAnalysisService, StructuredGradingAnalysisService>();
        services.AddSingleton<ICriteriaGenerationService, HeuristicCriteriaGenerationService>();
        services.AddSingleton<IAssignmentContextSelectionService, HeuristicAssignmentContextSelectionService>();
        services.AddSingleton<GradingBatchChannel>();
        services.AddScoped<GradingItemProcessor>();
        services.AddScoped<IGradingArtifactIngestionService, GradingArtifactIngestionService>();
        services.AddScoped<ISubmissionContentHashResolver, MoodleSubmissionContentHashResolver>();
        services.AddScoped<IGradingBatchOrchestrator, BackgroundGradingBatchOrchestrator>();
        services.AddScoped<IGradingContextBuilder, GradingContextBuilder>();
        services.AddHostedService<GradingBatchWorkerService>();
        services.AddHostedService<GradingPublicationWorkerService>();
        services.AddHostedService<GradingRetentionWorkerService>();

        return services;
    }
}
