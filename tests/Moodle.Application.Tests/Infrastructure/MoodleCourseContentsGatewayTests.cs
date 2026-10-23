using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Moodle.Infrastructure;
using Moodle.Presentation.Tools;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleCourseContentsGatewayTests
{
    [Fact]
    public async Task FileListingFiltersExternalEntriesAndEmptyModulesButGenericContentsRetainsThem()
    {
        var gateway = new MoodleCourseContentsGateway(Options.Create(new MoodleApiOptions()), new Credentials(), new Rest());
        var files = await gateway.GetCourseContentsAsync("1", "101", [], false, true, CancellationToken.None);
        var mixed = Assert.Single(Assert.Single(files.Sections).Modules);
        Assert.Equal("11", mixed.ModuleId);
        Assert.Equal("local.pdf", Assert.Single(mixed.Files).FileName);
        Assert.Equal(1, files.Sections[0].ModuleCount);
        Assert.False(files.Sections[0].IsEmpty);

        var all = await gateway.GetCourseContentsAsync("1", "101", [], false, false, CancellationToken.None);
        Assert.Equal(2, all.Sections[0].Modules.Count);
        Assert.Equal(3, all.Sections[0].Modules[0].Files.Count);
        Assert.Equal("url", all.Sections[0].Modules[1].ModuleType);
        var cached = MoodleCourseContentsTools.FilterCachedContents(all, [], true);
        var cachedModule = Assert.Single(cached.Sections[0].Modules);
        Assert.Equal(mixed.ModuleId, cachedModule.ModuleId);
        Assert.Equal(mixed.Files, cachedModule.Files);
        Assert.Equal(files.Sections[0].ModuleCount, cached.Sections[0].ModuleCount);
    }

    private sealed class Credentials : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials("client", "connection", "synthetic", "https://moodle.example", "synthetic", "synthetic", "synthetic", false));
    }
    private sealed class Rest : IMoodleRestClient
    {
        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            Assert.Equal("core_course_get_contents", functionName);
            return Task.FromResult(JsonSerializer.Deserialize<JsonElement>("""
                [{"id":1,"name":"Synthetic section","visible":1,"modules":[
                  {"id":11,"instance":22,"modname":"folder","name":"Mixed","visible":1,"uservisible":true,"contents":[
                    {"type":"file","filename":"local.pdf","fileurl":"https://moodle.example/pluginfile.php/1/local.pdf","isexternalfile":false},
                    {"type":"url","filename":"external","fileurl":"https://external.example"},
                    {"type":"file","filename":"remote.pdf","fileurl":"https://external.example/remote.pdf","isexternalfile":true}]},
                  {"id":12,"instance":23,"modname":"url","name":"External URL","visible":1,"uservisible":true,"contents":[
                    {"type":"url","fileurl":"https://external.example"}]}]}]
                """));
        }
        public Task<JsonElement> CallAsync(MoodleCredentials connection, string functionName, IReadOnlyDictionary<string, object?> parameters, bool allowServiceToken, CancellationToken cancellationToken) =>
            CallAsync(connection, functionName, parameters, cancellationToken);
    }
}
