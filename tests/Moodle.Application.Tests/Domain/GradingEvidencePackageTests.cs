using System.Text.Json;
using Moodle.Application.Grading.Packages;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Domain;

public sealed class GradingEvidencePackageTests
{
    [Fact]
    public void Identity_UsesAcademicEvidenceButNotPresentationFields()
    {
        var itemId = Guid.NewGuid();
        var first = GradingPackageIdentity.Create(
            "teacher-1",
            "connection-a",
            "client-a",
            "default",
            10,
            501,
            "1001",
            [new GradingPackageItemIdentity(itemId, "context-a", "submission-a")]);
        var sameEvidence = first with { };
        var changedEvidence = GradingPackageIdentity.Create(
            "teacher-1",
            "connection-a",
            "client-a",
            "default",
            10,
            501,
            "1001",
            [new GradingPackageItemIdentity(itemId, "context-a", "submission-b")]);

        Assert.Equal(first.PreparationKey, sameEvidence.PreparationKey);
        Assert.NotEqual(first.PreparationKey, changedEvidence.PreparationKey);
        Assert.DoesNotContain("default", first.PreparationKey, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Package_ChangesStateWithoutAllowingPublishedHistoryToBeOverwritten()
    {
        var package = GradingEvidencePackage.Create(
            "teacher-1",
            "connection-a",
            "client-a",
            "default",
            10,
            501,
            "1001",
            packageVersion: 1,
            preparationKey: "a".PadLeft(64, 'a'),
            activityKey: "c".PadLeft(64, 'c'),
            now: DateTimeOffset.UtcNow);

        package.MarkManifest("{}", "b".PadLeft(64, 'b'), DateTimeOffset.UtcNow);
        package.MarkReady("private/grading/a.zip", "c".PadLeft(64, 'c'), 123, 5, 10, 1, 0, 0, DateTimeOffset.UtcNow);

        Assert.Equal(GradingEvidencePackageStatus.Ready, package.Status);
        Assert.Throws<InvalidOperationException>(() => package.MarkNeedsAttention("late", "nao pode substituir pacote pronto"));
    }

    [Fact]
    public void PackageMember_PreservesItemIdentityWhenDisplayNameChanges()
    {
        var packageId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var member = GradingEvidencePackageMember.Create(
            packageId,
            itemId,
            101,
            "Ana Silva",
            9001,
            0,
            DateTimeOffset.UtcNow,
            "context-a",
            "submission-a");

        member.MarkIncluded();
        Assert.Equal(itemId, member.GradingItemId);
        Assert.Equal(GradingEvidencePackageMemberStatus.Included, member.Status);
    }

    [Fact]
    public void Package_RetryKeepsManifestHistoryAndExpiresOnlyTransportBytes()
    {
        var now = DateTimeOffset.UtcNow;
        var package = GradingEvidencePackage.Create(
            "teacher-1", "connection-a", "client-a", "default", 10, 501, "1001", 1,
            new string('a', 64), new string('b', 64), now: now);
        package.MarkManifest("{}", new string('c', 64), now);
        package.MarkReady("packages/p.zip", new string('d', 64), 10, 20, 3, 1, 1, 0, now, now.AddDays(1));

        package.MarkBlobExpired(now.AddDays(2));

        Assert.Equal(GradingEvidencePackageStatus.Ready, package.Status);
        Assert.Null(package.BlobKey);
        Assert.Null(package.ZipSha256);
        Assert.Equal(new string('c', 64), package.ManifestHash);
    }

    [Fact]
    public void Package_SchedulesTransientRetryBeforeNeedsAttention()
    {
        var now = DateTimeOffset.UtcNow;
        var package = GradingEvidencePackage.Create(
            "teacher-1", "connection-a", "client-a", "default", 10, 501, "1001", 1,
            new string('a', 64), new string('b', 64), now: now);

        Assert.True(package.TryAcquireLease("worker", now, TimeSpan.FromMinutes(5)));
        package.ScheduleRetry("moodle_unavailable", "Moodle indisponivel.", now.AddSeconds(30), now);

        Assert.Equal(GradingEvidencePackageStatus.Queued, package.Status);
        Assert.Equal(now.AddSeconds(30), package.NextAttemptAt);
        Assert.Equal(1, package.AttemptCount);
    }

    [Fact]
    public void Descriptor_ExposesPreparationErrorDetails()
    {
        var package = GradingEvidencePackage.Create(
            "teacher-1",
            "connection-a",
            "client-a",
            "default",
            10,
            501,
            "1001",
            1,
            new string('a', 64),
            new string('b', 64));
        package.MarkNeedsAttention("artifact_read_failed", "O arquivo da entrega nao pode ser lido.");

        var json = JsonSerializer.SerializeToDocument(GradingPackageDescriptorMapper.ToDescriptor(package)).RootElement;

        Assert.Equal("artifact_read_failed", json.GetProperty("lastErrorCode").GetString());
        Assert.Equal("O arquivo da entrega nao pode ser lido.", json.GetProperty("lastErrorMessage").GetString());
    }
}
