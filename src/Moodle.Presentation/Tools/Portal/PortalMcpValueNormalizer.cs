namespace Moodle.Presentation.Tools.Portal;

internal static class PortalMcpValueNormalizer
{
    public static string NormalizeTaskStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "in_progress" => "in_progress",
        "blocked" => "blocked",
        "done" => "done",
        "cancelled" => "cancelled",
        _ => "todo"
    };

    public static string NormalizeTaskPriority(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "high" => "high",
        "urgent" => "urgent",
        _ => "medium"
    };

    public static string NormalizeCalendarEventType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "meeting" or "alignment" or "delivery" or "training" or "webclass" => value!.Trim().ToLowerInvariant(),
        _ => "other"
    };

    public static string RequireTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("O título é obrigatório.", nameof(value));

        var title = value.Trim();
        if (title.Length > 240)
            throw new ArgumentException("O título deve ter no máximo 240 caracteres.", nameof(value));

        return title;
    }

    public static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var description = value.Trim();
        if (description.Length > 4000)
            throw new ArgumentException("A descrição deve ter no máximo 4000 caracteres.", nameof(value));

        return description;
    }
}
