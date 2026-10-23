using System.Text.Json;
using ModelContextProtocol.Protocol;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;

namespace Moodle.Presentation.Tools;

/// <summary>
/// Shared MCP result factory. Every failure receives a stable code and correlation id.
/// </summary>
internal static class ToolResultHelper
{
    internal static CallToolResult Error<T>(
        string message,
        string status = "error",
        string? errorCode = null,
        string? auditId = null,
        string? errorPhase = null,
        string? nextAction = null,
        string? code = null,
        string? causeCode = null,
        bool? retryable = null,
        int? retryAfterSeconds = null)
    {
        var normalizedCauseCode = MoodleErrorContract.NormalizeCode(
            causeCode ?? errorCode ?? MoodleErrorContract.InferCodeFromMessage(message));
        var normalizedOperationCode = NormalizeOperationCode(code ?? DefaultOperationCodeForPhase(errorPhase));
        var correlationId = string.IsNullOrWhiteSpace(auditId)
            ? Guid.NewGuid().ToString("N")
            : auditId;
        var response = new ToolResponse<T>(
            status,
            Data: default,
            Warnings: [message],
            AuditId: correlationId,
            DateTimeOffset.UtcNow,
            normalizedCauseCode,
            message,
            ErrorPhase: errorPhase,
            NextAction: nextAction,
            Code: normalizedOperationCode,
            CauseCode: normalizedCauseCode,
            Retryable: retryable ?? (errorPhase switch
            {
                "save" or "preview" or "publication" => false,
                _ => null
            }),
            RetryAfterSeconds: retryAfterSeconds);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = message }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = true
        };
    }

    internal static CallToolResult Error<T>(
        MoodleApiException error,
        string? errorPhase = null,
        string? nextAction = null,
        string? code = null,
        bool? retryable = null,
        int? retryAfterSeconds = null) =>
        Error<T>(
            MoodleErrorContract.SafeMessage(error.ErrorCode),
            errorCode: error.ErrorCode,
            auditId: error.AuditId,
            errorPhase: errorPhase,
            nextAction: nextAction,
            code: code,
            retryable: retryable,
            retryAfterSeconds: retryAfterSeconds);

    internal static CallToolResult Error<T>(
        Exception error,
        string? errorPhase = null,
        string? nextAction = null,
        string? code = null,
        bool? retryable = null,
        int? retryAfterSeconds = null)
    {
        var descriptor = MoodleErrorContract.Describe(error);
        return Error<T>(
            descriptor.Message,
            errorCode: descriptor.ErrorCode,
            auditId: descriptor.AuditId,
            errorPhase: errorPhase,
            nextAction: nextAction,
            code: code,
            retryable: retryable,
            retryAfterSeconds: retryAfterSeconds);
    }

    private static string? NormalizeOperationCode(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 80 ||
            !normalized.StartsWith("GRADING_", StringComparison.Ordinal) ||
            normalized.Any(character => !(character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
        {
            return null;
        }

        return normalized;
    }

    private static string? DefaultOperationCodeForPhase(string? phase) => phase switch
    {
        "query" => "GRADING_STATUS_QUERY_FAILED",
        "acquisition" => "GRADING_SUBMISSION_ACQUISITION_FAILED",
        "context" => "GRADING_CONTEXT_BUILD_FAILED",
        "package" => "GRADING_PACKAGE_MATERIALIZATION_FAILED",
        "analysis" => "GRADING_ANALYSIS_FAILED",
        "save" => "GRADING_DRAFT_SAVE_FAILED",
        "preview" => "GRADING_PUBLICATION_PREVIEW_FAILED",
        "publication" => "GRADING_PUBLICATION_FAILED",
        _ => null
    };
}
