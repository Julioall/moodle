namespace Moodle.Application.Abstractions;

public sealed record RegisterAccountRequest(string Name, string Email, string Password);
public sealed record LoginAccountRequest(string Email, string Password);
public sealed record ChangePasswordRequest(Guid UserId, string CurrentPassword, string NewPassword);
public sealed record PortalAccountListItemDto(Guid Id, string Name, string Email, DateTimeOffset CreatedAtUtc);
public sealed record ConnectMoodleAccountRequest(Guid UserId, string MoodleAlias, string MoodleBaseUrl, string MoodleUsername, string MoodlePassword, bool IsDefault, bool CanWrite);
public sealed record UpdateMoodleAccountRequest(Guid UserId, string MoodleId, string MoodleAlias, string MoodleBaseUrl, string? MoodleUsername, string? MoodlePassword, bool IsDefault, bool CanWrite);
public sealed record DeleteAccountRequest(Guid UserId, string Password, string ConfirmationText);
public sealed record AdminDeleteAccountsRequest(Guid ActorUserId, IReadOnlyList<Guid> UserIds, string Password, string ConfirmationText);
public sealed record AdminDeleteAccountsResultDto(int DeletedAccounts, int DeletedConnections, int DeletedTasks, int DeletedEvents, int DeletedReports);
public sealed record AccountDto(Guid Id, string Name, string Email, bool HasMoodleConnected, string? ConnectorClientId);
public sealed record MoodleConnectionDto(
    string Id,
    string Alias,
    string BaseUrl,
    bool IsDefault,
    bool CanWrite,
    string Status = "unknown",
    DateTimeOffset? LastValidatedAt = null);
public sealed record MoodleConnectionValidationDto(string Status, DateTimeOffset? LastValidatedAt);
public sealed record MoodleConnectionDataSummaryDto(int Memories, int Documents, int MoodleUserLinks, int AuditLogsRetained);
public sealed record AccountProfileDto(Guid Id, string Name, string Email, bool HasMoodleConnected, string? ApiKey, IReadOnlyList<MoodleConnectionDto> MoodleConnections);

public interface IAccountService
{
    Task<AccountDto> RegisterAsync(RegisterAccountRequest request, CancellationToken cancellationToken);
    Task<AccountDto?> ValidateLoginAsync(LoginAccountRequest request, CancellationToken cancellationToken);
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<PortalAccountListItemDto>> ListAccountsAsync(CancellationToken cancellationToken);
    Task ResetPasswordToDefaultAsync(Guid userId, CancellationToken cancellationToken);
    Task<AccountProfileDto?> GetProfileAsync(Guid userId, CancellationToken cancellationToken);
    Task<string> ConnectMoodleAsync(ConnectMoodleAccountRequest request, CancellationToken cancellationToken);
    Task<MoodleConnectionValidationDto> ValidateMoodleAsync(Guid userId, string moodleId, CancellationToken cancellationToken);
    Task<MoodleConnectionDataSummaryDto> GetMoodleDataSummaryAsync(Guid userId, string moodleId, CancellationToken cancellationToken);
    Task<string> RotateApiKeyAsync(Guid userId, CancellationToken cancellationToken);
    Task UpdateMoodleAsync(UpdateMoodleAccountRequest request, CancellationToken cancellationToken);
    Task DeleteMoodleAsync(Guid userId, string moodleId, CancellationToken cancellationToken);
    Task DeleteMoodleAsync(Guid userId, string moodleId, bool deleteLinkedData, string? confirmationText, CancellationToken cancellationToken);
    Task DeleteAccountAsync(DeleteAccountRequest request, CancellationToken cancellationToken);
    Task<AdminDeleteAccountsResultDto> DeleteAccountsAsAdminAsync(AdminDeleteAccountsRequest request, CancellationToken cancellationToken);
}
