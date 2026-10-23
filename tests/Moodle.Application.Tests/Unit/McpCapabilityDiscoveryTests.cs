using Microsoft.Extensions.Logging.Abstractions;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Presentation.Configuration;

namespace Moodle.Application.Tests.Unit;

public sealed class McpCapabilityDiscoveryTests
{
    [Fact]
    public async Task Uses_explicit_aliases_without_default_and_restores_selection()
    {
        var selection = new Selection { Alias = "original" };
        var catalog = new Functions(selection, alias => alias switch
        {
            "first" => Profile("first", "core_course_get_contents"),
            "second" => Profile("second", "mod_forum_add_discussion"),
            _ => throw new InvalidOperationException("A default connection must not be used.")
        });
        var snapshot = await Create(new Aliases("first", "second"), selection, catalog)
            .DiscoverAsync(CancellationToken.None);

        Assert.Equal(new[] { "first", "second" }, catalog.Calls);
        Assert.Equal("original", selection.Alias);
        Assert.Null(snapshot.GetHiddenReason("mod_forum_add_discussion", readOnly: false));
        Assert.Equal("required_capabilities_unavailable",
            snapshot.GetHiddenReason("core_course_get_contents mod_forum_add_discussion", readOnly: true));
    }

    [Fact]
    public async Task Partial_discovery_keeps_unknown_reads_and_requires_verified_writes()
    {
        var selection = new Selection();
        var catalog = new Functions(selection, alias => alias == "broken"
            ? throw new MoodleApiException(MoodleErrorContract.NetworkError, "Unavailable")
            : Profile("working", "mod_forum_add_discussion"));
        var snapshot = await Create(new Aliases("broken", "working"), selection, catalog)
            .DiscoverAsync(CancellationToken.None);

        Assert.Null(snapshot.GetHiddenReason("core_course_get_contents", readOnly: true));
        Assert.Null(snapshot.GetHiddenReason("mod_forum_add_discussion", readOnly: false));
        Assert.Equal("capability_discovery_incomplete",
            snapshot.GetHiddenReason("core_message_send_instant_messages", readOnly: false));
        Assert.Null(selection.Alias);
    }

    [Fact]
    public async Task Connection_lookup_failure_is_unknown_not_empty()
    {
        var selection = new Selection();
        var snapshot = await Create(new FailedAliases(), selection,
            new Functions(selection, _ => throw new InvalidOperationException("Must not run")))
            .DiscoverAsync(CancellationToken.None);

        Assert.Null(snapshot.GetHiddenReason("mod_assign_get_submissions", readOnly: true));
        Assert.Equal("capability_discovery_incomplete",
            snapshot.GetHiddenReason("mod_forum_add_discussion", readOnly: false));
    }

    [Fact]
    public async Task Cancellation_propagates_and_restores_selection()
    {
        var selection = new Selection { Alias = "original" };
        var service = Create(new Aliases("first"), selection,
            new Functions(selection, _ => throw new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.DiscoverAsync(CancellationToken.None));
        Assert.Equal("original", selection.Alias);
    }

    private static McpCapabilityDiscovery Create(IMoodleConnectionCatalog aliases, Selection selection, Functions functions) =>
        new(aliases, selection, functions, NullLogger<McpCapabilityDiscovery>.Instance);

    private static MoodleFunctionProfile Profile(string alias, params string[] functions) =>
        new(alias, alias, null, null, null,
            functions.Select(name => new MoodleFunctionDescriptor(name, MoodleFunctionRisk.Read, true)).ToArray(),
            DateTimeOffset.UtcNow);

    private sealed class Selection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private sealed class Aliases(params string[] aliases) : IMoodleConnectionCatalog
    {
        public Task<IReadOnlyList<string>> GetActiveAliasesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(aliases);
    }

    private sealed class FailedAliases : IMoodleConnectionCatalog
    {
        public Task<IReadOnlyList<string>> GetActiveAliasesAsync(CancellationToken cancellationToken) =>
            throw new MoodleApiException(MoodleErrorContract.ConnectionNotFound, "Missing client context");
    }

    private sealed class Functions(Selection selection, Func<string, MoodleFunctionProfile> resolve) : IMoodleFunctionCatalog
    {
        public List<string> Calls { get; } = [];
        public Task<MoodleFunctionProfile> GetCurrentAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            var alias = selection.Alias ?? "<default>";
            Calls.Add(alias);
            return Task.FromResult(resolve(alias));
        }
    }
}
