using System.Text.Json;
using Moodle.Application.Memory;
using Moodle.Presentation.Tools.Memory;
using ModelContextProtocol.Server;
using Moodle.Application.Tools;
using System.Reflection;

namespace Moodle.Application.Tests.Tools.Memory;

public sealed class MoodleMemoryToolsTests
{
    [Fact]
    public async Task Salvar_delega_campos_ao_servico_e_retorna_resposta_estruturada()
    {
        var service = new FakeMemoryService();
        var sut = new MoodleMemoryTools(service);

        var result = await sut.ManageAsync("salvar", "preferencia", "formato", "resposta curta", "explicit", moodleAlias: "goias", courseId: "42");

        Assert.False(result.IsError ?? false);
        Assert.Equal(new SaveUserMemoryRequest("preferencia", "formato", "resposta curta", "explicit", "goias", "42"), service.SaveRequest);
        var data = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
        Assert.Equal("salvar", data.GetProperty("action").GetString());
        Assert.Equal(service.Memory.Id, data.GetProperty("memory").GetProperty("id").GetGuid());
        Assert.Equal("preferencia", data.GetProperty("memory").GetProperty("category").GetString());
        Assert.False(data.GetProperty("memory").TryGetProperty("ownerSubject", out _));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("memories").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("removed").ValueKind);
    }

    [Fact]
    public async Task Listar_delega_todos_os_filtros()
    {
        var service = new FakeMemoryService();
        var sut = new MoodleMemoryTools(service);

        var result = await sut.ManageAsync("listar", category: "decisao", query: "rubrica", moodleAlias: "goias", courseId: "42", limit: 7);

        Assert.False(result.IsError ?? false);
        Assert.Equal(new ListUserMemoriesRequest("goias", "42", 7, "decisao", "rubrica"), service.ListRequest);
        var data = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
        Assert.Equal("listar", data.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("memory").ValueKind);
        Assert.Equal(1, data.GetProperty("memories").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("removed").ValueKind);
    }

    [Fact]
    public async Task Remover_exige_uuid_e_delega_ao_servico()
    {
        var service = new FakeMemoryService();
        var sut = new MoodleMemoryTools(service);

        var result = await sut.ManageAsync("remover", memoryId: service.Memory.Id);

        Assert.False(result.IsError ?? false);
        Assert.Equal(service.Memory.Id, service.RemovedId);
        var data = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
        Assert.Equal("remover", data.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("memory").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("memories").ValueKind);
        Assert.True(data.GetProperty("removed").GetBoolean());
    }

    [Fact]
    public void Metadata_declara_schema_estavel_e_remocao_destrutiva()
    {
        var method = typeof(MoodleMemoryTools).GetMethod(nameof(MoodleMemoryTools.ManageAsync))!;
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.True(attribute.Destructive);
        Assert.Equal(typeof(ToolResponse<MemoryToolResponse>), attribute.OutputSchemaType);
        Assert.Contains("remove", method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("salvar")]
    [InlineData("remover")]
    [InlineData("desconhecida")]
    public async Task Validacao_retorna_erro_controlado_sem_stack_trace(string action)
    {
        var sut = new MoodleMemoryTools(new FakeMemoryService());

        var result = await sut.ManageAsync(action);

        Assert.True(result.IsError ?? false);
        var warning = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("warnings")[0].GetString();
        Assert.False(string.IsNullOrWhiteSpace(warning));
        Assert.DoesNotContain(" at ", warning, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeMemoryService : IUserMemoryService
    {
        public UserMemoryDto Memory { get; } = new(Guid.NewGuid(), "preferencia", "formato", "resposta curta", "explicit", "goias", "42", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public SaveUserMemoryRequest? SaveRequest { get; private set; }
        public ListUserMemoriesRequest? ListRequest { get; private set; }
        public Guid? RemovedId { get; private set; }

        public Task<UserMemoryDto> SaveAsync(SaveUserMemoryRequest request, CancellationToken cancellationToken = default)
        { SaveRequest = request; return Task.FromResult(Memory); }

        public Task<IReadOnlyList<UserMemoryDto>> ListAsync(ListUserMemoriesRequest request, CancellationToken cancellationToken = default)
        { ListRequest = request; return Task.FromResult<IReadOnlyList<UserMemoryDto>>([Memory]); }

        public Task<RemoveUserMemoryResult> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        { RemovedId = id; return Task.FromResult(new RemoveUserMemoryResult(true)); }
    }
}
