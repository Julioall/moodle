using System.Security.Claims;
using System.Text.Json;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelContextProtocol.Server;
using Moodle.Infrastructure;
using Moodle.Presentation.Tools.Portal;

namespace Moodle.Application.Tests.Tools.Portal;

public sealed class PortalTaskAndAgendaToolsTests
{
    [Fact]
    public async Task Tarefas_suportam_criacao_consulta_edicao_e_remocao()
    {
        await using var fixture = await PortalFixture.CreateAsync();
        var dueAt = DateTimeOffset.UtcNow.AddDays(2);

        var created = await fixture.Tasks.CreateAsync(
            "Preparar aula",
            "Revisar materiais",
            priority: "high",
            dueAt: dueAt);
        Assert.False(created.IsError ?? false, ExtractMessage(created));
        var taskId = Data(created).GetProperty("task").GetProperty("id").GetGuid();

        var listed = await fixture.Tasks.ListAsync();
        Assert.False(listed.IsError ?? false, ExtractMessage(listed));
        var listedTask = Assert.Single(Data(listed).GetProperty("tasks").EnumerateArray());
        Assert.Equal("Preparar aula", listedTask.GetProperty("title").GetString());
        Assert.Equal("high", listedTask.GetProperty("priority").GetString());

        var updated = await fixture.Tasks.UpdateAsync(
            taskId,
            title: "Preparar aula revisada",
            status: "in_progress",
            clearDueAt: true);
        Assert.False(updated.IsError ?? false, ExtractMessage(updated));
        var updatedTask = Data(updated).GetProperty("task");
        Assert.Equal("Preparar aula revisada", updatedTask.GetProperty("title").GetString());
        Assert.Equal("in_progress", updatedTask.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, updatedTask.GetProperty("dueAt").ValueKind);

        var removed = await fixture.Tasks.RemoveAsync(taskId);
        Assert.False(removed.IsError ?? false, ExtractMessage(removed));
        Assert.True(Data(removed).GetProperty("removed").GetBoolean());
        Assert.False(await fixture.Db.Tasks.AnyAsync());
    }

    [Fact]
    public async Task Tarefa_nao_pode_ser_lida_ou_removida_por_outro_usuario()
    {
        await using var fixture = await PortalFixture.CreateAsync();
        var created = await fixture.Tasks.CreateAsync("Privada");
        var taskId = Data(created).GetProperty("task").GetProperty("id").GetGuid();

        var otherUserTools = fixture.ForUser(fixture.OtherUser);
        var listed = await otherUserTools.Tasks.ListAsync();
        Assert.Empty(Data(listed).GetProperty("tasks").EnumerateArray());

        var removed = await otherUserTools.Tasks.RemoveAsync(taskId);
        Assert.True(removed.IsError ?? false);
        Assert.True(await fixture.Db.Tasks.AnyAsync(task => task.Id == taskId));
    }

    [Fact]
    public async Task Agenda_suporta_criacao_consulta_edicao_e_remocao()
    {
        await using var fixture = await PortalFixture.CreateAsync();
        var startAt = DateTimeOffset.UtcNow.AddHours(4);
        var endAt = startAt.AddHours(1);

        var created = await fixture.Agenda.CreateAsync(
            "Reunião com a turma",
            startAt,
            endAt,
            "Alinhar a próxima aula",
            "meeting");
        Assert.False(created.IsError ?? false, ExtractMessage(created));
        var eventId = Data(created).GetProperty("event").GetProperty("id").GetGuid();

        var listed = await fixture.Agenda.ListAsync(startAt.AddHours(-1), endAt.AddHours(1));
        Assert.False(listed.IsError ?? false, ExtractMessage(listed));
        var listedEvent = Assert.Single(Data(listed).GetProperty("events").EnumerateArray());
        Assert.Equal("Reunião com a turma", listedEvent.GetProperty("title").GetString());
        Assert.Equal("meeting", listedEvent.GetProperty("type").GetString());

        var updated = await fixture.Agenda.UpdateAsync(
            eventId,
            title: "Reunião revisada",
            clearEndAt: true);
        Assert.False(updated.IsError ?? false, ExtractMessage(updated));
        var updatedEvent = Data(updated).GetProperty("event");
        Assert.Equal("Reunião revisada", updatedEvent.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, updatedEvent.GetProperty("endAt").ValueKind);

        var removed = await fixture.Agenda.RemoveAsync(eventId);
        Assert.False(removed.IsError ?? false, ExtractMessage(removed));
        Assert.True(Data(removed).GetProperty("removed").GetBoolean());
        Assert.False(await fixture.Db.CalendarEvents.AnyAsync());
    }

    [Fact]
    public async Task Metadados_expoem_permissao_do_portal_e_remocao_destrutiva()
    {
        var taskRemove = typeof(PortalTaskTools).GetMethod(nameof(PortalTaskTools.RemoveAsync))!;
        var taskMetadata = taskRemove.GetCustomAttributes(typeof(Moodle.Presentation.Configuration.MoodleToolMetadataAttribute), false)
            .Cast<Moodle.Presentation.Configuration.MoodleToolMetadataAttribute>()
            .Single();
        var taskTool = taskRemove.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal("tasks.manage", taskMetadata.RequiredPlatformPermission);
        Assert.True(taskTool.Destructive);

        var agendaRemove = typeof(PortalAgendaTools).GetMethod(nameof(PortalAgendaTools.RemoveAsync))!;
        var agendaMetadata = agendaRemove.GetCustomAttributes(typeof(Moodle.Presentation.Configuration.MoodleToolMetadataAttribute), false)
            .Cast<Moodle.Presentation.Configuration.MoodleToolMetadataAttribute>()
            .Single();
        var agendaTool = agendaRemove.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal("agenda.manage", agendaMetadata.RequiredPlatformPermission);
        Assert.True(agendaTool.Destructive);
    }

    private static JsonElement Data(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.False(result.IsError ?? false, result.Content?.FirstOrDefault()?.ToString());
        return Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
    }

    private static string ExtractMessage(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.StructuredContent is JsonElement element && element.TryGetProperty("message", out var message)
            ? message.GetString() ?? "MCP error"
            : "MCP error";

    private sealed class PortalFixture : IAsyncDisposable
    {
        private readonly DbContextOptions<ConnectorDbContext> options;
        private readonly Dictionary<Guid, PortalTools> toolsByUser;

        private PortalFixture(
            DbContextOptions<ConnectorDbContext> options,
            ConnectorDbContext db,
            UserAccountEntity user,
            UserAccountEntity otherUser,
            Dictionary<Guid, PortalTools> toolsByUser)
        {
            this.options = options;
            Db = db;
            User = user;
            OtherUser = otherUser;
            this.toolsByUser = toolsByUser;
            Tasks = toolsByUser[user.Id].Tasks;
            Agenda = toolsByUser[user.Id].Agenda;
        }

        public ConnectorDbContext Db { get; }
        public UserAccountEntity User { get; }
        public UserAccountEntity OtherUser { get; }
        public PortalTaskTools Tasks { get; }
        public PortalAgendaTools Agenda { get; }

        public static async Task<PortalFixture> CreateAsync()
        {
            var databaseRoot = new InMemoryDatabaseRoot();
            var options = new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase($"portal-mcp-{Guid.NewGuid():N}", databaseRoot)
                .Options;
            var db = new ConnectorDbContext(options);
            var user = new UserAccountEntity { Id = Guid.NewGuid(), Name = "Professor", Email = "professor@example.com", PasswordHash = "hash", ConnectorClientId = "client-professor" };
            var otherUser = new UserAccountEntity { Id = Guid.NewGuid(), Name = "Outro", Email = "outro@example.com", PasswordHash = "hash", ConnectorClientId = "client-outro" };
            db.UserAccounts.AddRange(user, otherUser);
            await db.SaveChangesAsync();

            var toolsByUser = new Dictionary<Guid, PortalTools>
            {
                [user.Id] = CreateTools(options, user),
                [otherUser.Id] = CreateTools(options, otherUser)
            };
            return new PortalFixture(options, db, user, otherUser, toolsByUser);
        }

        public PortalTools ForUser(UserAccountEntity user) => toolsByUser[user.Id];

        public async ValueTask DisposeAsync()
        {
            foreach (var tools in toolsByUser.Values)
                await tools.DisposeAsync();
            await Db.DisposeAsync();
        }

        private static PortalTools CreateTools(DbContextOptions<ConnectorDbContext> options, UserAccountEntity user)
        {
            var db = new ConnectorDbContext(options);
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Email, user.Email)
                ], "test"))
            };
            var accessor = new FixedHttpContextAccessor { HttpContext = context };
            var resolver = new PortalMcpIdentityResolver(accessor, db);
            return new PortalTools(db, resolver);
        }
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class PortalTools(ConnectorDbContext db, PortalMcpIdentityResolver resolver) : IAsyncDisposable
    {
        public PortalTaskTools Tasks { get; } = new(db, resolver);
        public PortalAgendaTools Agenda { get; } = new(db, resolver);

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }
}
