using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Integration;

public class ReportTemplateApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionFlagGatesCatalogButPrivateJobAndHistoryRemainAvailable(bool enabled)
    {
        using var factory = new Factory(enabled);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/reports/catalog")).StatusCode);
        var email = $"report-api-{Guid.NewGuid():N}@example.invalid";
        var password = Guid.NewGuid().ToString("N") + "!A1";
        using var registered = await Send(client, HttpMethod.Post, "/api/account/register", new { name = "Synthetic report owner", email, password });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        using var me = await client.GetAsync("/api/account/me");
        using var account = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        var owner = account.RootElement.GetProperty("id").GetGuid();
        using var permission = await Send(client, HttpMethod.Put, $"/api/users/{owner}/platform-permissions", new { permission = "reports.view", isAllowed = true });
        Assert.Equal(HttpStatusCode.OK, permission.StatusCode);
        using var login = await Send(client, HttpMethod.Post, "/api/account/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var session = JsonDocument.Parse(await client.GetStringAsync("/api/session"));
        Assert.Equal(enabled, session.RootElement.GetProperty("data").GetProperty("reportTemplatesEnabled").GetBoolean());
        using var catalog = await client.GetAsync("/api/reports/catalog");
        Assert.Equal(enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound, catalog.StatusCode);
        if (enabled)
        {
            using var body = JsonDocument.Parse(await catalog.Content.ReadAsStringAsync());
            Assert.Equal(30, body.RootElement.GetProperty("fields").GetArrayLength());
        }
        var jobId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            db.ReportJobs.Add(new ReportJobEntity { Id = jobId, OwnerId = owner, ReportType = "template", ScopeType = "courses",
                ConnectionAlias = "synthetic", Status = "completed", TemplateNameSnapshot = "Historical synthetic model",
                CourseIdsJson = "[\"1\",\"2\"]",
                RequestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var detailResponse = await client.GetAsync($"/api/reports/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "1", "2" }, detail.RootElement.GetProperty("courseIds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("definitionSnapshot").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("selectionSnapshot").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/reports/jobs/{Guid.NewGuid()}")).StatusCode);
        using var history = JsonDocument.Parse(await client.GetStringAsync("/api/reports/jobs?search=Historical&status=completed&connectionRef=synthetic&pageSize=1"));
        Assert.Single(history.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/reports/jobs?status=invalid")).StatusCode);
    }
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, object body)
    {
        using var csrf = JsonDocument.Parse(await client.GetStringAsync("/api/csrf"));
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }
    [Fact]
    public async Task JobDetailsAndSearchCannotExposeAnotherOwnersHistory()
    {
        using var factory = new Factory(true);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var email = $"report-isolation-{Guid.NewGuid():N}@example.invalid";
        var password = Guid.NewGuid().ToString("N") + "!A1";
        using var registered = await Send(client, HttpMethod.Post, "/api/account/register", new { name = "Synthetic reader", email, password });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/account/me"));
        var owner = me.RootElement.GetProperty("id").GetGuid();
        using var permission = await Send(client, HttpMethod.Put, $"/api/users/{owner}/platform-permissions", new { permission = "reports.view", isAllowed = true });
        Assert.Equal(HttpStatusCode.OK, permission.StatusCode);
        using var login = await Send(client, HttpMethod.Post, "/api/account/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var foreignJob = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            db.ReportJobs.Add(new ReportJobEntity { Id = foreignJob, OwnerId = Guid.NewGuid(), ReportType = "template",
                ScopeType = "courses", Status = "completed", TemplateNameSnapshot = "Unique foreign synthetic model",
                RequestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/reports/jobs/{foreignJob}")).StatusCode);
        using var history = JsonDocument.Parse(await client.GetStringAsync("/api/reports/jobs?search=Unique%20foreign"));
        Assert.Empty(history.RootElement.GetProperty("data").EnumerateArray());
        using var refresh = await Send(client, HttpMethod.Post, "/api/reports/jobs", new { refreshFromJobId = foreignJob });
        Assert.Equal(HttpStatusCode.NotFound, refresh.StatusCode);
        using var revoke = await Send(client, HttpMethod.Put, $"/api/users/{owner}/platform-permissions", new { permission = "reports.view", isAllowed = false });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        using var relogin = await Send(client, HttpMethod.Post, "/api/account/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/reports/catalog")).StatusCode);
    }
    private sealed class Factory(bool enabled) : McpTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Reports:TemplatesEnabled"] = enabled.ToString() }));
        }
    }
}
