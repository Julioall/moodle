using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Integration;

public sealed class MoodleCapabilitiesTransportIntegrationTests
{
    [Fact]
    public async Task Catalog_ObtemPerfisDiferentesDeDoisMoodlesSimuladosPeloRestClient()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.Run(async context =>
        {
            Assert.Equal("/webservice/rest/server.php", context.Request.Path);
            var form = await context.Request.ReadFormAsync();
            Assert.Equal("core_webservice_get_site_info", form["wsfunction"]);
            Assert.DoesNotContain("wstoken", context.Request.QueryString.Value, StringComparison.OrdinalIgnoreCase);

            var response = form["wstoken"] == "token-goias"
                ? "{\"sitename\":\"Goiás\",\"release\":\"4.5\",\"userid\":7,\"functions\":[{\"name\":\"core_course_get_courses_by_field\"},{\"name\":\"core_course_get_enrolled_courses_by_timeline_classification\"},{\"name\":\"mod_assign_get_assignments\"}]}"
                : "{\"sitename\":\"SENAI\",\"release\":\"5.1.2\",\"userid\":8,\"functions\":[{\"name\":\"core_enrol_get_users_courses\"},{\"name\":\"mod_assign_get_assignments\"},{\"name\":\"mod_assign_get_submissions\"}]}";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response, Encoding.UTF8);
        });
        await app.StartAsync();

        var credentials = new SwitchingCredentialsProvider();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var httpClient = app.GetTestClient();
        using var telemetry = new McpToolUsageMetrics();
        using var restServices = new ServiceCollection()
            .AddSingleton<IMoodleRestClient>(new MoodleRestClient(
                httpClient,
                new SwitchingTokenProvider(),
                NullLogger<MoodleRestClient>.Instance))
            .BuildServiceProvider();
        var coalescer = new MoodleFunctionProfileCoalescer(cache, restServices.GetRequiredService<IServiceScopeFactory>(), telemetry);
        var catalog = new MoodleFunctionCatalog(cache, credentials, coalescer, telemetry);

        credentials.Current = "goias";
        var goias = await catalog.GetCurrentAsync(false, CancellationToken.None);
        credentials.Current = "senai";
        var senai = await catalog.GetCurrentAsync(false, CancellationToken.None);

        Assert.Equal("4.5", goias.Release);
        Assert.Contains(goias.Functions, function => function.Name == "core_course_get_enrolled_courses_by_timeline_classification");
        Assert.Equal("5.1.2", senai.Release);
        Assert.Contains(senai.Functions, function => function.Name == "core_enrol_get_users_courses");

        var flows = new MoodleBusinessFlowRegistry();
        Assert.Equal("timeline", flows.Evaluate("listar_cursos_ativos", goias).SelectedStrategy);
        Assert.Equal("enrolled_courses_fallback", flows.Evaluate("listar_cursos_ativos", senai).SelectedStrategy);
        Assert.True(flows.Evaluate("listar_entregas_aguardando_correcao", senai).IsAvailable);
    }

    private sealed class SwitchingCredentialsProvider : IMoodleCredentialsProvider
    {
        public string Current { get; set; } = "goias";

        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials("client", Current, Current, "http://localhost", "user", "password", Current, false));
    }

    private sealed class SwitchingTokenProvider : IMoodleAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(
            MoodleCredentials connection,
            CancellationToken cancellationToken) =>
            Task.FromResult(connection.Alias == "goias" ? "token-goias" : "token-senai");

        public void Invalidate(MoodleCredentials connection)
        {
        }
    }
}
