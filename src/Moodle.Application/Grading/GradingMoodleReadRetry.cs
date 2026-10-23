using Moodle.Application.MoodleApi;

namespace Moodle.Application.Grading;

/// <summary>
/// Retry curto para leituras Moodle usadas pelo worker. Não repete erros de
/// permissão, autenticação, função ausente ou resposta inválida.
/// </summary>
internal static class GradingMoodleReadRetry
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Action<Exception, int>? onRetry,
        CancellationToken cancellationToken)
        => await MoodleReadRetry.ExecuteAsync(operation, onRetry, cancellationToken);

    public static bool IsTransient(Exception exception)
        => MoodleReadRetry.IsTransient(exception);
}
