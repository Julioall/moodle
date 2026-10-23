using Moodle.Application.MoodleApi;

namespace Moodle.Application.Grading.Packages;

public static class GradingPackageRetryPolicy
{
    /// <summary>
    /// Classifica falhas que podem ser recuperadas pelo worker sem transformar
    /// uma indisponibilidade externa ou de storage em bloqueio permanente.
    /// A quantidade de tentativas continua limitada pelo pacote.
    /// </summary>
    public static bool IsTransient(Exception exception) =>
        exception is GradingTransientDependencyException or IOException ||
        GradingMoodleReadRetry.IsTransient(exception) ||
        IsRateLimited(exception);

    public static bool IsRateLimited(Exception exception) => exception switch
    {
        MoodleApiException moodle => moodle.HttpStatusCode == 429,
        HttpRequestException http => (int?)http.StatusCode == 429,
        GradingTransientDependencyException transient when transient.InnerException is not null =>
            IsRateLimited(transient.InnerException),
        _ => false
    };

    public static TimeSpan GetDelay(
        Exception exception,
        int attempt,
        int baseSeconds,
        bool includeJitter = true)
    {
        var retryAfter = FindRetryAfter(exception);
        if (retryAfter is { } requestedDelay && requestedDelay >= TimeSpan.Zero)
        {
            return requestedDelay > TimeSpan.FromSeconds(60)
                ? TimeSpan.FromSeconds(60)
                : requestedDelay;
        }

        return GetDelay(attempt, baseSeconds, includeJitter);
    }

    public static TimeSpan GetDelay(
        int attempt,
        int baseSeconds,
        bool includeJitter = true)
    {
        var normalizedAttempt = Math.Clamp(attempt, 1, 16);
        var normalizedBase = Math.Clamp(baseSeconds, 1, 3600);
        var seconds = Math.Min(
            24 * 60 * 60,
            normalizedBase * Math.Pow(2, normalizedAttempt - 1));
        var jitter = includeJitter
            ? Random.Shared.NextDouble() * Math.Min(seconds * 0.2, 60)
            : 0;
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private static TimeSpan? FindRetryAfter(Exception exception) => exception switch
    {
        MoodleApiException moodle when moodle.HttpStatusCode == 429 => moodle.RetryAfter,
        GradingTransientDependencyException transient when transient.InnerException is not null =>
            FindRetryAfter(transient.InnerException),
        _ => null
    };
}
