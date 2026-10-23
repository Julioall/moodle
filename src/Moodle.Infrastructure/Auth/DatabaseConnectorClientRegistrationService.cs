using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

internal sealed class DatabaseConnectorClientRegistrationService(
    ConnectorDbContext dbContext,
    IConnectorSecretProtector secretProtector) : IConnectorClientRegistrationService
{
    public async Task<RegisterConnectorClientResult> RegisterOrRotateAsync(
        RegisterConnectorClientRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
        {
            throw new ArgumentException("ClientId e obrigatorio.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.MoodleUsername) || string.IsNullOrWhiteSpace(request.MoodlePassword))
        {
            throw new ArgumentException("Credenciais Moodle sao obrigatorias.", nameof(request));
        }

        if (!Uri.TryCreate(request.MoodleBaseUrl.Trim(), UriKind.Absolute, out var moodleBaseUri) ||
            moodleBaseUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(moodleBaseUri.Host) ||
            !string.IsNullOrEmpty(moodleBaseUri.UserInfo))
        {
            throw new ArgumentException("MoodleBaseUrl deve ser uma URL HTTPS absoluta e sem credenciais na URL.", nameof(request));
        }

        var apiKey = GenerateApiKey();
        var apiKeyHash = ApiKeyHasher.Hash(apiKey);
        var now = DateTimeOffset.UtcNow;
        var clientId = request.ClientId.Trim();
        var alias = MoodleConnectionAlias.NormalizeOrDefault(request.MoodleAlias);
        var normalizedBaseUrl = NormalizeBaseUrl(moodleBaseUri);
        var connectionId = string.Empty;

        var existingConnections = await dbContext.ConnectorClients
            .Where(client => client.ClientId == clientId)
            .ToListAsync(cancellationToken);
        var entity = existingConnections.SingleOrDefault(client => client.Id == connectionId);
        if (entity is null)
        {
            var canonicalMatches = existingConnections
                .Where(client => MoodleConnectionAlias.Normalize(client.MoodleAlias) == alias)
                .ToArray();
            if (canonicalMatches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Mais de uma conexao existente corresponde ao alias canonico '{alias}'. Saneie os aliases duplicados antes de reconectar.");
            }

            entity = canonicalMatches.SingleOrDefault();
        }

        var replaced = entity is not null;
        if (entity is null)
        {
            entity = new ConnectorClientCredentialEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                ClientId = clientId,
                CreatedAtUtc = now
            };
            dbContext.ConnectorClients.Add(entity);
        }

        var hasExistingApiKey = await dbContext.ConnectorClients
            .AnyAsync(client => client.ClientId == clientId && client.ApiKeyHash != null && client.Id != entity.Id, cancellationToken);

        entity.ApiKeyHash = hasExistingApiKey ? entity.ApiKeyHash : apiKeyHash;
        entity.ClientId = clientId;
        entity.MoodleAlias = alias;
        entity.MoodleBaseUrl = normalizedBaseUrl;
        entity.MoodleUsernameEncrypted = secretProtector.Protect(request.MoodleUsername.Trim());
        entity.MoodlePasswordEncrypted = secretProtector.Protect(request.MoodlePassword);
        entity.MoodleTarget = MoodleConnectionAlias.NormalizeOrDefault(request.MoodleTarget);
        entity.IsDefault = request.IsDefault || !await dbContext.ConnectorClients.AnyAsync(client => client.ClientId == clientId && client.IsActive && client.Id != entity.Id, cancellationToken);
        entity.CanWrite = request.CanWrite;
        entity.IsActive = true;
        entity.UpdatedAtUtc = now;

        if (entity.IsDefault)
        {
            await dbContext.ConnectorClients
                .Where(client => client.ClientId == clientId && client.Id != entity.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(client => client.IsDefault, false), cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new RegisterConnectorClientResult(clientId, entity.Id, alias, entity.ApiKeyHash == apiKeyHash ? apiKey : string.Empty, replaced);
    }

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string NormalizeBaseUrl(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

}
