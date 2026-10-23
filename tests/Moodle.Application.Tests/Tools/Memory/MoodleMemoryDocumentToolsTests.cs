using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Moodle.Application.Memory;
using Moodle.Application.Tools;
using Moodle.Presentation.Tools.Memory;

namespace Moodle.Application.Tests.Tools.Memory;

public sealed class MoodleMemoryDocumentToolsTests
{
    [Fact]
    public async Task Salvar_delega_documento_e_retorna_conteudo_completo()
    {
        var service = new FakeDocumentService();
        var sut = new MoodleMemoryDocumentTools(service);

        var result = await sut.SalvarAsync("cronograma", "Cronograma", "<table>...</table>", "html", "explicit", "senai", "42");

        Assert.False(result.IsError ?? false);
        Assert.Equal(new SaveUserMemoryDocumentRequest("cronograma", "Cronograma", "<table>...</table>", "html", "explicit", "senai", "42"), service.SaveRequest);
        var data = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
        Assert.Equal(service.Document.Id, data.GetProperty("document").GetProperty("id").GetGuid());
        Assert.Equal("<table>...</table>", data.GetProperty("document").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Ler_exige_document_id_e_delega_ao_servico()
    {
        var service = new FakeDocumentService();
        var sut = new MoodleMemoryDocumentTools(service);

        var result = await sut.LerAsync(service.Document.Id);

        Assert.False(result.IsError ?? false);
        Assert.Equal(service.Document.Id, service.ReadId);
        var data = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
        Assert.Equal("markdown", data.GetProperty("document").GetProperty("format").GetString());
    }

    [Fact]
    public async Task Listar_delega_filtros_e_nao_exige_conteudo()
    {
        var service = new FakeDocumentService();
        var sut = new MoodleMemoryDocumentTools(service);

        var result = await sut.ListarAsync(query: "cronograma", moodleAlias: "senai", courseId: "42", limit: 5);

        Assert.False(result.IsError ?? false);
        Assert.Equal(new ListUserMemoryDocumentsRequest("senai", "42", 5, "cronograma"), service.ListRequest);
        var data = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
        Assert.Equal(1, data.GetProperty("documents").GetArrayLength());
    }

    [Fact]
    public async Task Remover_exige_document_id_e_delega_ao_servico()
    {
        var service = new FakeDocumentService();
        var sut = new MoodleMemoryDocumentTools(service);

        var result = await sut.RemoverAsync(service.Document.Id);

        Assert.False(result.IsError ?? false);
        Assert.Equal(service.Document.Id, service.RemovedId);
        var data = Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data");
        Assert.True(data.GetProperty("removed").GetBoolean());
    }

    [Fact]
    public void Metadata_declara_tools_dedicadas_com_risco_por_operacao()
    {
        var salvar = typeof(MoodleMemoryDocumentTools).GetMethod(nameof(MoodleMemoryDocumentTools.SalvarAsync))!
            .GetCustomAttribute<McpServerToolAttribute>()!;
        var listar = typeof(MoodleMemoryDocumentTools).GetMethod(nameof(MoodleMemoryDocumentTools.ListarAsync))!
            .GetCustomAttribute<McpServerToolAttribute>()!;
        var ler = typeof(MoodleMemoryDocumentTools).GetMethod(nameof(MoodleMemoryDocumentTools.LerAsync))!
            .GetCustomAttribute<McpServerToolAttribute>()!;
        var remover = typeof(MoodleMemoryDocumentTools).GetMethod(nameof(MoodleMemoryDocumentTools.RemoverAsync))!
            .GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Equal("save_user_memory_document", salvar.Name);
        Assert.False(salvar.ReadOnly);
        Assert.True(salvar.Destructive);
        Assert.True(salvar.Idempotent);

        Assert.Equal("list_user_memory_documents", listar.Name);
        Assert.True(listar.ReadOnly);
        Assert.False(listar.Destructive);

        Assert.Equal("read_user_memory_document", ler.Name);
        Assert.True(ler.ReadOnly);
        Assert.False(ler.Destructive);

        Assert.Equal("remove_user_memory_document", remover.Name);
        Assert.False(remover.ReadOnly);
        Assert.True(remover.Destructive);
        Assert.True(remover.Idempotent);
    }

    private sealed class FakeDocumentService : IUserMemoryDocumentService
    {
        public UserMemoryDocumentDto Document { get; } = new(Guid.NewGuid(), "cronograma", "Cronograma", "# Cronograma", "markdown", "explicit", "senai", "42", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public SaveUserMemoryDocumentRequest? SaveRequest { get; private set; }
        public ListUserMemoryDocumentsRequest? ListRequest { get; private set; }
        public Guid? ReadId { get; private set; }
        public Guid? RemovedId { get; private set; }

        public Task<UserMemoryDocumentDto> SaveAsync(SaveUserMemoryDocumentRequest request, CancellationToken cancellationToken = default)
        {
            SaveRequest = request;
            return Task.FromResult(Document with
            {
                Title = request.Title,
                Content = request.Content,
                Format = request.Format,
                Origin = request.Origin,
                MoodleAlias = request.MoodleAlias,
                CourseId = request.CourseId
            });
        }

        public Task<UserMemoryDocumentDto?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
        {
            ReadId = id;
            return Task.FromResult<UserMemoryDocumentDto?>(Document);
        }

        public Task<IReadOnlyList<UserMemoryDocumentDto>> ListAsync(ListUserMemoryDocumentsRequest request, CancellationToken cancellationToken = default)
        {
            ListRequest = request;
            return Task.FromResult<IReadOnlyList<UserMemoryDocumentDto>>([Document]);
        }

        public Task<RemoveUserMemoryDocumentResult> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            RemovedId = id;
            return Task.FromResult(new RemoveUserMemoryDocumentResult(true));
        }
    }
}
