using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Moodle.Application.Configuration;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;

namespace Moodle.Presentation.Tools;

/// <summary>ZIP de evidencias privado, autorizado pelo proprietario do pacote.</summary>
[McpServerResourceType]
public sealed class MoodleGradingPackageResources(
    IGradingPackageStore packageStore,
    IGradingPackageBlobStore blobStore,
    ICurrentUserContext currentUser,
    IOptions<GradingLimitsOptions>? limits = null)
{
    private readonly long maxResourceBytes = ResolveMaxResourceBytes(limits);

    [McpServerResource(
        UriTemplate = "moodle://grading-package/{packageId}",
        Name = "Grading evidence package",
        MimeType = "application/zip")]
    public async Task<IEnumerable<ResourceContents>> ReadPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(packageId, out var id) || id == Guid.Empty)
        {
            throw new InvalidOperationException("O identificador do pacote e invalido.");
        }

        var package = await packageStore.GetAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Pacote de correcao nao encontrado.");
        if (!CanAccess(package.OwnerSubject))
        {
            throw new UnauthorizedAccessException("Usuario atual nao esta autorizado a ler este pacote de correcao.");
        }

        if (package.Status is not (Domain.Grading.GradingEvidencePackageStatus.Ready or
            Domain.Grading.GradingEvidencePackageStatus.PartiallyReady or
            Domain.Grading.GradingEvidencePackageStatus.ResultsReceived))
        {
            throw new InvalidOperationException("O pacote ainda nao possui ZIP disponivel.");
        }

        if (string.IsNullOrWhiteSpace(package.BlobKey))
        {
            throw new InvalidOperationException("O ZIP do pacote nao esta disponivel; execute prepare_grading_packages com regenerateTransport=true para reconstruir somente o transporte. O manifesto e os rascunhos continuam preservados.");
        }

        if (package.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("O ZIP do pacote expirou; execute prepare_grading_packages com regenerateTransport=true para reconstruir somente o transporte. O manifesto e os rascunhos continuam preservados.");
        }

        if (package.ZipSizeBytes is long zipSizeBytes && zipSizeBytes > maxResourceBytes)
        {
            throw new InvalidOperationException("O ZIP do pacote excede o limite seguro de leitura; execute prepare_grading_packages com regenerateTransport=true ou use os resources individuais de submission.");
        }

        await using var input = await blobStore.OpenReadAsync(package.BlobKey, cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[128 * 1024];
        var totalBytes = 0L;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (totalBytes > maxResourceBytes - read)
            {
                throw new InvalidOperationException("O ZIP do pacote excede o limite seguro de leitura; use os resources individuais de submission ou solicite uma nova preparacao.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalBytes += read;
        }

        var bytes = output.ToArray();
        if (!string.IsNullOrWhiteSpace(package.ZipSha256))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, package.ZipSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A integridade do ZIP do pacote nao pode ser confirmada; use os resources individuais ou solicite uma nova preparacao.");
            }
        }

        return [BlobResourceContents.FromBytes(
            bytes,
            $"moodle://grading-package/{package.Id:D}",
            "application/zip")];
    }

    private bool CanAccess(string ownerSubject) =>
        string.Equals(ownerSubject, currentUser.Subject, StringComparison.Ordinal) ||
        currentUser.HasScope("grading.admin") ||
        currentUser.HasPlatformPermission("tool.assignments.grade");

    private static long ResolveMaxResourceBytes(IOptions<GradingLimitsOptions>? options)
    {
        var configured = options?.Value.MaxResourceBytes ?? 25 * 1024 * 1024;
        return configured > 0
            ? configured
            : throw new InvalidOperationException("O limite de leitura do resource ZIP deve ser positivo.");
    }
}
