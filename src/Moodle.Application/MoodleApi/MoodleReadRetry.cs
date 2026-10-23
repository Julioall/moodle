namespace Moodle.Application.MoodleApi;

/// <summary>
/// Short retry policy for idempotent Moodle reads. It deliberately excludes
/// permission, authentication, missing-function and invalid-response errors.
/// Callers must not use it around Moodle writes or local resource mutations.
/// </summary>
public static class MoodleReadRetry
{
    private const int MaxAttempts = 3;
    private static readonly AsyncLocal<int> RetryScopeDepth = new();

    /// <summary>
    /// True while a logical read retry owns the attempt budget. The HTTP
    /// transport uses this to avoid multiplying its retries inside the same
    /// logical operation.
    /// </summary>
    public static bool IsActive => RetryScopeDepth.Value > 0;

    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Action<Exception, int>? onRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var isNested = IsActive;
        using var correlationScope = MoodleReadRetryCorrelationScope.BeginInvocation();
        using var invocationScope = MoodleReadAttemptBudgetScope.BeginInvocation();
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var attemptScope = MoodleReadAttemptBudgetScope.BeginAttempt();
                RetryScopeDepth.Value++;
                try
                {
                    return await operation(cancellationToken);
                }
                finally
                {
                    RetryScopeDepth.Value--;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                MoodleReadRetryCorrelationScope.RecordTransientFailure();

                // Nested helpers only own a retry loop when the transport has
                // registered a shared per-read budget. Without one, the outer
                // helper remains the sole retry owner.
                if (isNested && !MoodleReadAttemptBudgetScope.HasBudgetForCurrentInvocation())
                    throw;

                if (attempt >= MaxAttempts)
                {
                    // Preserve the original transport/Moodle error so callers
                    // can still return its structured error code and audit id.
                    throw;
                }

                McpReadInvocationScope.RecordLogicalRetry(exception, attempt);
                onRetry?.Invoke(exception, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("A leitura Moodle nao foi concluida.");
    }

    public static bool IsTransient(Exception exception)
    {
        return exception switch
        {
            HttpRequestException => true,
            TimeoutException => true,
            TaskCanceledException => true,
            MoodleApiException moodle =>
                MoodleErrorContract.NormalizeCode(moodle.ErrorCode) is
                    MoodleErrorContract.NetworkError or MoodleErrorContract.RequestTimeout,
            _ => false,
        };
    }
}
