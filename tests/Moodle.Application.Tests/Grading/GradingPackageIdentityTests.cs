using Moodle.Application.Grading.Packages;
using Xunit;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingPackageIdentityTests
{
    [Fact]
    public void Legacy_identity_uses_alias_as_connection_scope_when_stable_ids_are_missing()
    {
        var first = CreateIdentity(connectionId: null, clientId: null, alias: "turma-a");
        var second = CreateIdentity(connectionId: null, clientId: null, alias: "turma-b");

        Assert.NotEqual(first.ActivityKey, second.ActivityKey);
        Assert.NotEqual(first.PreparationKey, second.PreparationKey);
    }

    [Fact]
    public void Stable_connection_identity_allows_alias_renames_without_rebuilding_package()
    {
        var beforeRename = CreateIdentity(connectionId: "connection-1", clientId: null, alias: "antigo");
        var afterRename = CreateIdentity(connectionId: "connection-1", clientId: null, alias: "novo");

        Assert.Equal(beforeRename.ActivityKey, afterRename.ActivityKey);
        Assert.Equal(beforeRename.PreparationKey, afterRename.PreparationKey);
    }

    private static GradingPackageIdentity CreateIdentity(
        string? connectionId,
        string? clientId,
        string? alias) =>
        GradingPackageIdentity.Create(
            "teacher@example.com",
            connectionId,
            clientId,
            alias,
            courseId: 10,
            assignmentId: 501,
            cmid: "1001",
            [new GradingPackageItemIdentity(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "context-hash",
                "submission-hash")]);
}
