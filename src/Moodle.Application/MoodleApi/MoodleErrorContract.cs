using System.Security.Cryptography;
using System.Text.Json;
using Moodle.Application.Abstractions;

namespace Moodle.Application.MoodleApi;

public sealed record MoodleErrorDescriptor(
    string ErrorCode,
    string Message,
    string AuditId,
    int? HttpStatusCode = null);

public static class MoodleErrorContract
{
    public const string ConnectionNotFound = "moodle_connection_not_found";
    public const string DefaultConnectionNotConfigured = "moodle_default_connection_not_configured";
    public const string ConnectionDisabled = "moodle_connection_disabled";
    public const string TokenMissing = "moodle_token_missing";
    public const string TokenDecryptionFailed = "moodle_token_decryption_failed";
    public const string AuthenticationFailed = "moodle_authentication_failed";
    public const string FunctionNotAllowed = "moodle_function_not_allowed";
    public const string WriteScopeNotRegistered = "moodle_write_scope_not_registered";
    public const string PermissionDenied = "moodle_permission_denied";
    public const string RequestTimeout = "moodle_request_timeout";
    public const string NetworkError = "moodle_network_error";
    public const string InvalidResponse = "moodle_invalid_response";
    public const string ApiError = "moodle_api_error";
    public const string ValidationError = "validation_error";
    public const string PolicyRedirect = "policy_redirect";
    public const string PendingActionNotFound = "pending_action_not_found";
    public const string DestinationLocked = "destination_locked";
    public const string InvalidPendingAction = "invalid_pending_action";
    public const string BusinessRuleViolation = "business_rule_violation";
    public const string ScormNotFound = "scorm_not_found";
    public const string ScormSelectionRequired = "scorm_selection_required";
    public const string ScormPackageUnavailable = "scorm_package_unavailable";
    public const string ScormPackageNotFound = "scorm_package_not_found";
    public const string ScormPackageAccessDenied = "scorm_package_access_denied";
    public const string ScormPackageDownloadFailed = "scorm_package_download_failed";
    public const string ScormPackageUrlInvalid = "scorm_package_url_invalid";
    public const string ScormPackageTooLarge = "scorm_package_too_large";
    public const string ScormManifestMissing = "scorm_manifest_missing";
    public const string InvalidScormPackage = "invalid_scorm_package";
    public const string InvalidScormManifest = "invalid_scorm_manifest";
    public const string InvalidScormResponse = "invalid_scorm_response";
    public const string CourseNotFound = "moodle_course_not_found";
    public const string ActivityNotFound = "moodle_activity_not_found";
    public const string ModuleNotFound = "moodle_module_not_found";
    public const string ResourceNotFound = "moodle_resource_not_found";
    public const string AssignmentNotFound = "assignment_not_found";
    public const string InvalidFilter = "invalid_filter";
    public const string InvalidPage = "invalid_page";
    public const string UnknownMoodleFunction = "unknown_moodle_function";
    public const string SnapshotUnavailable = "snapshot_unavailable";
    public const string ReadQueueFull = "moodle_read_queue_full";
    public const string ReadQueueTimeout = "moodle_read_queue_timeout";
    public const string ReadConnectionCapacity = "moodle_read_connection_capacity";
    public const string MoodleAdmissionRejected = "moodle_admission_rejected";
    public const string Unexpected = "unexpected_connector_error";

    public static MoodleErrorDescriptor Describe(Exception exception)
    {
        if (exception is MoodleApiException moodle)
        {
            var stableCode = NormalizeCode(moodle.ErrorCode);
            return new MoodleErrorDescriptor(stableCode, SafeMessage(stableCode), moodle.AuditId, moodle.HttpStatusCode);
        }

        var code = exception switch
        {
            MoodleAdmissionException => MoodleAdmissionRejected,
            OperationCanceledException => RequestTimeout,
            HttpRequestException => NetworkError,
            CryptographicException or FormatException => TokenDecryptionFailed,
            JsonException => InvalidResponse,
            ArgumentException => ValidationError,
            UnauthorizedAccessException => PermissionDenied,
            InvalidOperationException invalidOperation => ClassifyInvalidOperation(invalidOperation.Message),
            _ => Unexpected
        };

        return new MoodleErrorDescriptor(code, SafeMessage(code), Guid.NewGuid().ToString("N"));
    }

    public static string NormalizeCode(string? errorCode)
    {
        var code = errorCode?.Trim().ToLowerInvariant().Replace('-', '_');
        return code switch
        {
            ConnectionNotFound => ConnectionNotFound,
            DefaultConnectionNotConfigured => DefaultConnectionNotConfigured,
            ConnectionDisabled => ConnectionDisabled,
            TokenMissing or "moodle_credentials_missing" => TokenMissing,
            TokenDecryptionFailed or "moodle_credentials_decryption_failed" => TokenDecryptionFailed,
            AuthenticationFailed or "invalid_token" or "invalidtoken" or "invalid_login" or "invalidlogin" => AuthenticationFailed,
            FunctionNotAllowed or "function_not_available" or "function_not_discovered" or
                "function_not_read_safe" or "function_not_allowed" or "webservice_function_not_allowed" or
                "flow_unavailable" => FunctionNotAllowed,
            WriteScopeNotRegistered => WriteScopeNotRegistered,
            PermissionDenied or "access_exception" or "accessexception" or "webservice_access_exception" or
                "nopermissions" or "not_enrolled" => PermissionDenied,
            RequestTimeout or "timeout" or "moodle_timeout" => RequestTimeout,
            NetworkError or "moodle_unavailable" => NetworkError,
            InvalidResponse or "moodle_empty_response" => InvalidResponse,
            CourseNotFound or "invalidcourseid" or "course_not_found" => CourseNotFound,
            ActivityNotFound or "activity_not_found" or "invalidactivityid" => ActivityNotFound,
            ModuleNotFound or "module_not_found" or "invalidcoursemodule" => ModuleNotFound,
            ResourceNotFound => ResourceNotFound,
            AssignmentNotFound or "assignment_not_found" => AssignmentNotFound,
            InvalidFilter or "invalid_filter" => InvalidFilter,
            InvalidPage or "invalid_page" => InvalidPage,
            UnknownMoodleFunction or "unknown_function" => UnknownMoodleFunction,
            SnapshotUnavailable => SnapshotUnavailable,
            ReadQueueFull => ReadQueueFull,
            ReadQueueTimeout => ReadQueueTimeout,
            ReadConnectionCapacity => ReadConnectionCapacity,
            MoodleAdmissionRejected or "moodle_admission_queue_full" or "moodle_admission_queue_timeout" or
                "moodle_admission_deadline" or "moodle_admission_state_capacity" or
                "moodle_attempt_budget_exhausted" => MoodleAdmissionRejected,
            ApiError or "moodle_error" or "invalidparameter" or "invalid_parameter" => ApiError,
            ValidationError => ValidationError,
            PolicyRedirect => PolicyRedirect,
            PendingActionNotFound => PendingActionNotFound,
            DestinationLocked => DestinationLocked,
            InvalidPendingAction => InvalidPendingAction,
            BusinessRuleViolation => BusinessRuleViolation,
            ScormNotFound => ScormNotFound,
            ScormSelectionRequired => ScormSelectionRequired,
            ScormPackageUnavailable => ScormPackageUnavailable,
            ScormPackageNotFound => ScormPackageNotFound,
            ScormPackageAccessDenied => ScormPackageAccessDenied,
            ScormPackageDownloadFailed => ScormPackageDownloadFailed,
            ScormPackageUrlInvalid => ScormPackageUrlInvalid,
            ScormPackageTooLarge => ScormPackageTooLarge,
            ScormManifestMissing => ScormManifestMissing,
            InvalidScormPackage => InvalidScormPackage,
            InvalidScormManifest => InvalidScormManifest,
            InvalidScormResponse => InvalidScormResponse,
            Unexpected => Unexpected,
            _ => ApiError
        };
    }

    public static string InferCodeFromMessage(string? message)
    {
        var normalized = message?.Trim() ?? string.Empty;
        if (normalized.StartsWith("Informe ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Provide ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Os parametros devem", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationError;
        }

        return Unexpected;
    }

    public static string SafeMessage(string errorCode) => NormalizeCode(errorCode) switch
    {
        ConnectionNotFound => "A conexao Moodle solicitada nao foi encontrada para esta conta.",
        DefaultConnectionNotConfigured => "Nenhuma conexao Moodle padrao esta configurada para esta conta.",
        ConnectionDisabled => "A conexao Moodle solicitada esta desativada.",
        TokenMissing => "A conexao Moodle nao possui credenciais suficientes para obter um token.",
        TokenDecryptionFailed => "As credenciais da conexao Moodle nao puderam ser descriptografadas.",
        AuthenticationFailed => "O Moodle recusou as credenciais da conexao selecionada.",
        FunctionNotAllowed => "A funcao necessaria nao esta autorizada nesta conexao Moodle.",
        WriteScopeNotRegistered => "A funcao Moodle nao possui um escopo de escrita explicitamente registrado.",
        PermissionDenied => "O usuario autenticado nao possui permissao para esta leitura no Moodle.",
        RequestTimeout => "O Moodle nao respondeu dentro do tempo limite.",
        NetworkError => "Nao foi possivel estabelecer comunicacao com o Moodle.",
        InvalidResponse => "O Moodle retornou uma resposta invalida.",
        CourseNotFound => "O curso nao foi encontrado ou nao esta acessivel para o usuario autenticado.",
        ActivityNotFound => "A atividade nao foi encontrada no curso informado.",
        ModuleNotFound => "O modulo nao foi encontrado no curso informado.",
        ResourceNotFound => "O item solicitado nao foi encontrado entre os dados autorizados do usuario.",
        AssignmentNotFound => "A tarefa nao foi encontrada no curso informado.",
        InvalidFilter => "O filtro informado nao e valido.",
        InvalidPage => "A pagina informada nao e valida.",
        UnknownMoodleFunction => "A funcao Moodle informada nao pertence ao catalogo conhecido do Connector.",
        SnapshotUnavailable => "O snapshot solicitado ainda nao esta disponivel ou esta incompleto.",
        ReadQueueFull => "A fila de leituras Moodle desta conexao esta cheia. Tente novamente mais tarde.",
        ReadQueueTimeout => "O limite de espera da fila de leituras Moodle foi atingido.",
        ReadConnectionCapacity => "O limite de conexoes simultaneas acompanhadas foi atingido.",
        MoodleAdmissionRejected => "A chamada ao Moodle foi recusada por um limite local de saída. Tente novamente mais tarde.",
        ApiError => "O Moodle recusou ou nao conseguiu concluir a chamada solicitada.",
        ValidationError => "Os dados informados nao sao validos.",
        PolicyRedirect => "A operacao exige o fluxo controlado apropriado.",
        PendingActionNotFound => "A acao pendente nao foi encontrada.",
        DestinationLocked => "O destino desta execucao ja foi definido e nao pode ser alterado.",
        InvalidPendingAction => "A acao pendente nao esta em um estado valido para esta operacao.",
        BusinessRuleViolation => "A operacao nao pode ser concluida no estado atual.",
        ScormNotFound => "Nenhum pacote SCORM correspondente foi encontrado no curso.",
        ScormSelectionRequired => "O curso possui mais de um pacote SCORM; informe o identificador do pacote.",
        ScormPackageUnavailable => "O Moodle nao forneceu um pacote SCORM baixavel para esta atividade.",
        ScormPackageNotFound => "O arquivo do pacote SCORM nao foi encontrado no Moodle.",
        ScormPackageAccessDenied => "O Moodle negou acesso ao arquivo do pacote SCORM.",
        ScormPackageDownloadFailed => "Nao foi possivel baixar o arquivo do pacote SCORM.",
        ScormPackageUrlInvalid => "A URL do pacote SCORM nao pertence ao endpoint Moodle permitido.",
        ScormPackageTooLarge => "O pacote SCORM excede o limite configurado para leitura.",
        ScormManifestMissing => "O pacote SCORM nao contem imsmanifest.xml.",
        InvalidScormPackage => "O pacote baixado nao e um ZIP SCORM valido.",
        InvalidScormManifest => "O imsmanifest.xml nao pode ser lido com seguranca.",
        InvalidScormResponse => "O Moodle retornou uma resposta de SCORM invalida.",
        _ => "O conector encontrou um erro inesperado ao consultar o Moodle."
    };

    private static string ClassifyInvalidOperation(string? message)
    {
        var normalized = message?.Trim() ?? string.Empty;
        if (normalized.Contains("Policy Redirect", StringComparison.OrdinalIgnoreCase))
        {
            return PolicyRedirect;
        }

        if (normalized.Contains("acao pendente nao encontrada", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("ação pendente não encontrada", StringComparison.OrdinalIgnoreCase))
        {
            return PendingActionNotFound;
        }

        if (normalized.Contains("ja foi direcionada", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("já foi direcionada", StringComparison.OrdinalIgnoreCase))
        {
            return DestinationLocked;
        }

        if (normalized.Contains("so pode ser reconciliada", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("só pode ser reconciliada", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("estado atual", StringComparison.OrdinalIgnoreCase))
        {
            return InvalidPendingAction;
        }

        return Unexpected;
    }
}
