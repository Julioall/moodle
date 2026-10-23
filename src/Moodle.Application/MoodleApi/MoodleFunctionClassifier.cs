namespace Moodle.Application.MoodleApi;

/// <summary>
/// Classifies a discovered Moodle web-service function without maintaining a
/// per-function allowlist. Moodle's site-info response exposes the functions
/// enabled by the current token, but not their HTTP semantics. We therefore
/// allow only conventional query verbs to execute immediately; every other
/// discovered function is conservatively routed through the write
/// prepare/confirm workflow.
/// </summary>
public static class MoodleFunctionClassifier
{
    private static readonly string[] ReadVerbs =
    [
        "get", "list", "search", "find", "fetch", "view", "check", "can", "count", "export"
    ];

    private static readonly string[] MutationVerbs =
    [
        "add", "create", "update", "set", "save", "send", "mark", "mute", "unmute", "lock", "unlock",
        "toggle", "submit", "confirm", "decline", "block", "unblock", "delete", "remove", "purge",
        "reset", "unenrol", "enrol", "revert", "restore", "import", "move", "copy", "assign"
    ];

    public static MoodleFunctionRisk Classify(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return MoodleFunctionRisk.Unknown;
        }

        var words = functionName.Trim().Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return MoodleFunctionRisk.Unknown;
        }

        // In mod_assign_get_* the word "assign" identifies Moodle's module;
        // in core_enrol_get_* the word "enrol" identifies the enrollment
        // module. Neither is the mutation verb used by functions such as
        // core_role_assign_roles. Exclude only those module segments before
        // applying the conservative mutation check.
        var actionWords = words.Where((word, index) =>
            !(index == 1 && words.Length > 1 &&
              ((words[0].Equals("mod", StringComparison.OrdinalIgnoreCase) &&
                word.Equals("assign", StringComparison.OrdinalIgnoreCase)) ||
               (words[0].Equals("core", StringComparison.OrdinalIgnoreCase) &&
                word.Equals("enrol", StringComparison.OrdinalIgnoreCase)))));

        // A mutating term anywhere in the canonical Moodle function name wins.
        // This includes removal/destructive actions: they are not executed by
        // the read path, but can proceed through explicit confirmation.
        if (actionWords.Any(word => MutationVerbs.Any(verb => word.Equals(verb, StringComparison.OrdinalIgnoreCase))))
        {
            return MoodleFunctionRisk.ControlledWrite;
        }

        return words.Any(word => ReadVerbs.Any(verb => word.Equals(verb, StringComparison.OrdinalIgnoreCase)))
            ? MoodleFunctionRisk.Read
            : MoodleFunctionRisk.ControlledWrite;
    }
}
