using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Moodle.Infrastructure;

public sealed class PasswordResetOptions
{
    public const string SectionName = "PasswordReset";
    public bool Enabled { get; init; }
    public string SmtpHost { get; init; } = string.Empty;
    public int SmtpPort { get; init; } = 587;
    public string SmtpUsername { get; init; } = string.Empty;
    public string SmtpPassword { get; init; } = string.Empty;
    public string SenderAddress { get; init; } = string.Empty;
    public string PublicBaseUrl { get; init; } = string.Empty;
    public bool IsReady =>
        !string.IsNullOrWhiteSpace(SmtpHost) && SmtpPort is > 0 and <= 65535 &&
        !string.IsNullOrWhiteSpace(SmtpUsername) &&
        !string.IsNullOrWhiteSpace(SmtpPassword) &&
        MailAddress.TryCreate(SenderAddress, out _) &&
        Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var baseUri) &&
        baseUri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(baseUri.UserInfo) &&
        string.IsNullOrEmpty(baseUri.Query) && string.IsNullOrEmpty(baseUri.Fragment);
}

public interface IPasswordResetMailer
{
    Task SendAsync(string destination, string token, CancellationToken cancellationToken);
}

public sealed class SmtpPasswordResetMailer(IOptions<PasswordResetOptions> options) : IPasswordResetMailer
{
    public async Task SendAsync(string destination, string token, CancellationToken cancellationToken)
    {
        var opt = options.Value;
        if (!opt.Enabled || !opt.IsReady)
            throw new InvalidOperationException("Serviço de recuperação de senha não configurado.");
        var link = opt.PublicBaseUrl.TrimEnd('/') + "/?resetToken=" + Uri.EscapeDataString(token);
        using var message = new MailMessage(new MailAddress(opt.SenderAddress),
            new MailAddress(destination))
        {
            Subject = "Recuperação de senha do Moodle Conector",
            Body = "Uma recuperação de senha foi solicitada. O código expira em 15 minutos e só pode ser usado uma vez." +
                   "\n\nAbra este link para definir uma nova senha:\n" + link +
                   "\n\nCaso não tenha solicitado, ignore esta mensagem.",
            IsBodyHtml = false
        };
        using var smtp = new SmtpClient(opt.SmtpHost, opt.SmtpPort)
        {
            EnableSsl = true,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(opt.SmtpUsername, opt.SmtpPassword),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        await smtp.SendMailAsync(message, cancellationToken);
    }
}

public interface IAccountPasswordResetService
{
    bool IsEnabled { get; }
    Task RequestAsync(string? email, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(string? token, string? newPassword, CancellationToken cancellationToken);
}

public sealed class AccountPasswordResetService(
    ConnectorDbContext db,
    IPasswordResetMailer mailer,
    IOptions<PasswordResetOptions> options,
    ILogger<AccountPasswordResetService> logger) : IAccountPasswordResetService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public bool IsEnabled => options.Value.Enabled && options.Value.IsReady;

    public async Task RequestAsync(string? email, CancellationToken cancellationToken)
    {
        if (!IsEnabled) throw new InvalidOperationException("Recuperação de senha indisponível.");
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320) return;
        var account = await db.UserAccounts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Email == email.Trim().ToLowerInvariant(), cancellationToken);
        if (account is null) return;

        var now = DateTimeOffset.UtcNow;
        var recent = await db.PasswordResetTokens
            .CountAsync(item => item.UserId == account.Id && item.CreatedAtUtc >= now.AddHours(-1), cancellationToken);
        if (recent >= 3) return;

        var bytes = RandomNumberGenerator.GetBytes(32);
        var raw = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        CryptographicOperations.ZeroMemory(bytes);
        var item = new PasswordResetTokenEntity
        {
            Id = Guid.NewGuid(), UserId = account.Id,
            TokenHash = Digest(raw), CreatedAtUtc = now, ExpiresAtUtc = now.Add(Lifetime)
        };
        db.PasswordResetTokens.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await mailer.SendAsync(account.Email, raw, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never log the token or the destination; never disclose account existence.
            logger.LogWarning("Password reset delivery failed. FailureType={FailureType}", ex.GetType().Name);
            item.ConsumedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    public async Task<bool> CompleteAsync(string? token, string? newPassword, CancellationToken cancellationToken)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(token) || token.Length is < 40 or > 80 ||
            token.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            return false;
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8 || newPassword.Length > 256)
            throw new ArgumentException("A senha deve ter entre 8 e 256 caracteres.");

        var now = DateTimeOffset.UtcNow;
        var hash = Digest(token);
        var row = await db.PasswordResetTokens.SingleOrDefaultAsync(item =>
            item.TokenHash == hash && item.ConsumedAtUtc == null && item.ExpiresAtUtc > now, cancellationToken);
        if (row is null) return false;
        var account = await db.UserAccounts.FindAsync([row.UserId], cancellationToken);
        if (account is null) return false;

        row.ConsumedAtUtc = now; // EF concurrency token ensures one-time redemption.
        account.PasswordHash = PasswordHasher.Hash(newPassword);
        account.UpdatedAtUtc = now;
        account.ApiKeyEncrypted = null;

        var clientId = account.ConnectorClientId ?? account.Id.ToString();
        foreach (var client in await db.ConnectorClients.Where(item => item.ClientId == clientId).ToListAsync(cancellationToken))
            client.ApiKeyHash = null;
        foreach (var tokenRow in await db.OAuthTokens.Where(item => item.Subject == account.Id.ToString()).ToListAsync(cancellationToken))
            tokenRow.Status = "revoked";

        foreach (var other in await db.PasswordResetTokens.Where(item =>
            item.UserId == account.Id && item.Id != row.Id && item.ConsumedAtUtc == null).ToListAsync(cancellationToken))
            other.ConsumedAtUtc = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static string Digest(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
