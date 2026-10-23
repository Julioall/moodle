using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleForumGatewayTests
{
    [Fact]
    public async Task GetForumDiscussionsAsync_PrefereFuncaoAnunciadaPelaConexaoFieg()
    {
        var restClient = new FakeRestClient("{\"discussions\":[{\"id\":11,\"discussion\":22,\"name\":\"Aviso\",\"subject\":\"Aviso\"}]}");
        var gateway = CreateGateway(
            restClient,
            Profile("mod_forum_get_forum_discussions", "mod_forum_get_discussion_posts"));

        var discussions = await gateway.GetForumDiscussionsPaginatedAsync(
            "7", "701", "timemodified", "DESC", 1, 10, CancellationToken.None);

        var discussion = Assert.Single(discussions);
        Assert.Equal("22", discussion.DiscussionId);
        Assert.Equal("mod_forum_get_forum_discussions", restClient.FunctionName);
        Assert.Equal("701", restClient.Parameters["forumid"]);
        Assert.Equal(1, restClient.Parameters["sortorder"]);
        Assert.Equal(0, restClient.Parameters["page"]);
        Assert.Equal(10, restClient.Parameters["perpage"]);
        Assert.DoesNotContain("sortby", restClient.Parameters.Keys);
        Assert.DoesNotContain("sortdirection", restClient.Parameters.Keys);
    }

    [Fact]
    public async Task GetForumDiscussionsAsync_UsaVariantePaginadaSomenteQuandoAnunciada()
    {
        var restClient = new FakeRestClient("{\"discussions\":[{\"id\":11,\"discussion\":22,\"name\":\"Aviso\",\"subject\":\"Aviso\"}]}");
        var gateway = CreateGateway(
            restClient,
            Profile("mod_forum_get_forum_discussions_paginated"));

        await gateway.GetForumDiscussionsPaginatedAsync(
            "7", "701", "id", "ASC", 2, 5, CancellationToken.None);

        Assert.Equal("mod_forum_get_forum_discussions_paginated", restClient.FunctionName);
        Assert.Equal("id", restClient.Parameters["sortby"]);
        Assert.Equal("ASC", restClient.Parameters["sortdirection"]);
        Assert.Equal(1, restClient.Parameters["page"]);
        Assert.Equal(5, restClient.Parameters["perpage"]);
    }

    [Fact]
    public async Task GetForumDiscussionsAsync_FalhaQuandoNenhumaFuncaoCompativelFoiAnunciada()
    {
        var restClient = new FakeRestClient("{\"discussions\":[]}");
        var gateway = CreateGateway(restClient, Profile("mod_forum_get_discussion_posts"));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() => gateway.GetForumDiscussionsPaginatedAsync(
            "7", "701", "timemodified", "DESC", 1, 10, CancellationToken.None));

        Assert.Equal(MoodleErrorContract.FunctionNotAllowed, error.ErrorCode);
        Assert.Equal("mod_forum_get_forum_discussions", error.FunctionName);
        Assert.Null(restClient.FunctionName);
    }

    private static MoodleForumGateway CreateGateway(FakeRestClient restClient, MoodleFunctionProfile profile) =>
        new(
            Options.Create(new MoodleApiOptions()),
            new FakeCredentialsProvider(),
            restClient,
            new FakeFunctionCatalog(profile));

    private static MoodleFunctionProfile Profile(params string[] functionNames) => new(
        "connection",
        "fieg",
        "Moodle FIEG",
        "5.0",
        7,
        functionNames
            .Select(name => new MoodleFunctionDescriptor(name, MoodleFunctionRisk.Read, true))
            .ToArray(),
        DateTimeOffset.UtcNow);

    private sealed class FakeFunctionCatalog(MoodleFunctionProfile profile) : IMoodleFunctionCatalog
    {
        public Task<MoodleFunctionProfile> GetCurrentAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(profile);
    }

    private sealed class FakeCredentialsProvider : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials(
                "client", "connection", "fieg", "https://moodle.example", "user", "password", "fieg", false));
    }

    private sealed class FakeRestClient(string response) : IMoodleRestClient
    {
        public string? FunctionName { get; private set; }
        public IReadOnlyDictionary<string, object?> Parameters { get; private set; } = new Dictionary<string, object?>();

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, false, cancellationToken);

        public Task<JsonElement> CallAsync(
            MoodleCredentials connection,
            string functionName,
            IReadOnlyDictionary<string, object?> parameters,
            bool allowServiceToken,
            CancellationToken cancellationToken)
        {
            FunctionName = functionName;
            Parameters = new Dictionary<string, object?>(parameters);
            using var document = JsonDocument.Parse(response);
            return Task.FromResult(document.RootElement.Clone());
        }
    }
}
