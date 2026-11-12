namespace Moodle.Application.MoodleApi;

/// <summary>
/// Explicit allowlist of controlled Moodle write families and their
/// delegated OAuth scopes. New or destructive Moodle functions must be
/// reviewed and mapped before they can be prepared or confirmed.
/// </summary>
public static class MoodleWriteScopePolicy
{
    public static bool TryGetScope(string? functionName, out string scope)
    {
        var normalized = functionName?.Trim().ToLowerInvariant();
        scope = normalized switch
        {
            "core_message_send_instant_messages" or
            "core_message_send_messages_to_conversation" => "moodle.write.messages",
            "mod_forum_add_discussion" or
            "mod_forum_add_discussion_post" => "moodle.write.forums",
            "core_calendar_create_calendar_events" => "moodle.write.course_content",
            "mod_assign_save_grade" or
            "mod_assign_save_grades" => "moodle.write.assignments.grade",
            _ => string.Empty
        };

        return scope.Length > 0;
    }

    public static string ForFunction(string functionName)
    {
        if (TryGetScope(functionName, out var scope))
        {
            return scope;
        }

        if (string.IsNullOrWhiteSpace(functionName))
        {
            throw new ArgumentException("A função Moodle é obrigatória.", nameof(functionName));
        }

        // An arbitrary Moodle function must never inherit a broad delegated
        // write scope merely because another write capability was granted.
        throw new MoodleApiException(
            "moodle_write_function_unclassified",
            "A função Moodle não possui política explícita de escrita autorizada.");
    }
}

public static class MoodleWriteExecutionClassifier
{
    public static bool IsUnknown(Exception exception)
    {
        if (exception is HttpRequestException or TimeoutException)
        {
            return true;
        }

        return exception is MoodleApiException moodleException &&
            (moodleException.HttpStatusCode is >= 500 and <= 599 ||
             MoodleErrorContract.NormalizeCode(moodleException.ErrorCode) is
                 MoodleErrorContract.NetworkError or MoodleErrorContract.RequestTimeout);
    }
}
