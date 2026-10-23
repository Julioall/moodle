using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moodle.Infrastructure;
using Npgsql;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public class ReportTemplateCrudPostgresApiTests
{
    [Fact]
    public async Task CrudUsesRealPostgresRevisionLocationAndAntiforgery()
    {
        var connection = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection), "PostgreSQL real is required.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var database = "report_api_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
        builder.Database = database;
        try
        {
            // Prepare the disposable database before hosting starts its existing
            // workers. Starting a host against an empty schema is not a CRUD test.
            await using (var setup = new ConnectorDbContext(new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseNpgsql(builder.ConnectionString).Options))
                await setup.ApplyVersionedSchemaAsync();
            using var factory = new Factory(builder.ConnectionString);
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
            var email = $"crud-{Guid.NewGuid():N}@example.invalid";
            var password = Guid.NewGuid().ToString("N") + "!A1";
            using var register = await Send(client, HttpMethod.Post, "/api/account/register", new { name = "Synthetic CRUD owner", email, password });
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);
            using var me = JsonDocument.Parse(await client.GetStringAsync("/api/account/me"));
            var owner = me.RootElement.GetProperty("id").GetGuid();
            using var permission = await Send(client, HttpMethod.Put, $"/api/users/{owner}/platform-permissions", new { permission = "reports.view", isAllowed = true });
            Assert.Equal(HttpStatusCode.OK, permission.StatusCode);
            using var login = await Send(client, HttpMethod.Post, "/api/account/login", new { email, password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var initialList = JsonDocument.Parse(await client.GetStringAsync("/api/reports/templates"));
            var initialModels = initialList.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(6, initialModels.Length);
            Assert.Contains(initialModels, item => item.GetProperty("name").GetString() == "Preparação para conselho de classe");
            var definition = new { schemaVersion = 1, sourceId = "student_course", columns = new[] { new { fieldId = "student.name", label = "Aluno" } } };
            using var noCsrf = await client.PostAsJsonAsync("/api/reports/templates", new { name = "Denied", definition });
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            using var createTemplate = await Send(client, HttpMethod.Post, "/api/reports/templates", new { name = "Synthetic model", definition });
            Assert.Equal(HttpStatusCode.Created, createTemplate.StatusCode);
            var location = createTemplate.Headers.Location!.ToString();
            using var model = JsonDocument.Parse(await client.GetStringAsync(location));
            Assert.Equal(1, model.RootElement.GetProperty("revision").GetInt64());
            using var update = await Send(client, HttpMethod.Put, location, new { name = "Edited synthetic", definition, expectedRevision = 1 });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            using var conflict = await Send(client, HttpMethod.Put, location, new { name = "Stale edit", definition, expectedRevision = 1 });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var staleDelete = await Send(client, HttpMethod.Delete, location + "?expectedRevision=1", null);
            Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
            using var other = factory.CreateClient(new() { AllowAutoRedirect = false });
            var otherEmail = $"other-{Guid.NewGuid():N}@example.invalid";
            var otherPassword = Guid.NewGuid().ToString("N") + "!A1";
            using var otherRegister = await Send(other, HttpMethod.Post, "/api/account/register", new { name = "Synthetic second owner", email = otherEmail, password = otherPassword });
            Assert.Equal(HttpStatusCode.OK, otherRegister.StatusCode);
            using var otherAccount = JsonDocument.Parse(await other.GetStringAsync("/api/account/me"));
            var otherOwner = otherAccount.RootElement.GetProperty("id").GetGuid();
            using var otherPermission = await Send(client, HttpMethod.Put, $"/api/users/{otherOwner}/platform-permissions", new { permission = "reports.view", isAllowed = true });
            Assert.Equal(HttpStatusCode.OK, otherPermission.StatusCode);
            using var otherLogin = await Send(other, HttpMethod.Post, "/api/account/login", new { email = otherEmail, password = otherPassword });
            Assert.Equal(HttpStatusCode.OK, otherLogin.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(location)).StatusCode);
            using var foreignUpdate = await Send(other, HttpMethod.Put, location, new { name = "Foreign edit", definition, expectedRevision = 2 });
            Assert.Equal(HttpStatusCode.NotFound, foreignUpdate.StatusCode);
            using var foreignDelete = await Send(other, HttpMethod.Delete, location + "?expectedRevision=2", null);
            Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
            using var otherList = JsonDocument.Parse(await other.GetStringAsync("/api/reports/templates"));
            Assert.Equal(6, otherList.RootElement.GetProperty("items").GetArrayLength());
            using var delete = await Send(client, HttpMethod.Delete, location + "?expectedRevision=2", null);
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(location)).StatusCode);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, object? body)
    {
        using var csrf = JsonDocument.Parse(await client.GetStringAsync("/api/csrf"));
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }
    private sealed class Factory(string connection) : McpTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["Reports:TemplatesEnabled"] = "true", ["Logging:LogLevel:Default"] = "Warning",
                    ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ConnectorDbContext>();
                services.RemoveAll<DbContextOptions>();
                services.RemoveAll<DbContextOptions<ConnectorDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<ConnectorDbContext>>();
                services.AddDbContext<ConnectorDbContext>(options => { options.UseNpgsql(connection); options.UseOpenIddict(); });
            });
        }
    }
}
