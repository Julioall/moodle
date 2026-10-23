using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleBackgroundConnectionContextRestorerTests
{
    [Fact]
    public async Task TryRestoreAsync_restores_client_and_alias_for_legacy_batch()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        db.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = "connection-legacy",
            ClientId = "client-legacy",
            MoodleAlias = "goias",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var executionContext = new ConnectorExecutionContext();
        var selection = new MoodleConnectionSelection();
        var restorer = new MoodleBackgroundConnectionContextRestorer(
            db,
            executionContext,
            selection);

        var restored = await restorer.TryRestoreAsync(
            "connection-legacy",
            "teacher-legacy",
            CancellationToken.None);

        Assert.True(restored);
        Assert.Equal("client-legacy", executionContext.ClientId);
        Assert.Equal("teacher-legacy", executionContext.Subject);
        Assert.Equal("goias", selection.Alias);
    }

    [Fact]
    public async Task TryRestoreAsync_does_not_restore_disabled_or_unknown_connection()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        db.ConnectorClients.Add(new ConnectorClientCredentialEntity
        {
            Id = "connection-disabled",
            ClientId = "client-disabled",
            MoodleAlias = "goias",
            IsActive = false
        });
        await db.SaveChangesAsync();

        var executionContext = new ConnectorExecutionContext();
        var selection = new MoodleConnectionSelection();
        var restorer = new MoodleBackgroundConnectionContextRestorer(
            db,
            executionContext,
            selection);

        Assert.False(await restorer.TryRestoreAsync(
            "connection-disabled",
            "teacher",
            CancellationToken.None));
        Assert.False(await restorer.TryRestoreAsync(
            "connection-missing",
            "teacher",
            CancellationToken.None));
        Assert.Null(executionContext.ClientId);
        Assert.Null(selection.Alias);
    }
}
