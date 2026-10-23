using Moodle.Presentation;

namespace Moodle.Application.Tests.Tools;

public sealed class MoodleInstructionsTests
{
    [Fact]
    public void Instrucoes_cobrem_fluxo_automatico_privacidade_origem_e_limite_do_cliente()
    {
        var text = MoodleInstructions.Text.ToLowerInvariant();
        Assert.Contains("memorias", text);
        Assert.Contains("orientacoes pedagogicas", text);
        Assert.Contains("automaticamente", text);
        Assert.Contains("modelo", text);
        Assert.Contains("documento", text);
        Assert.Contains("explicit", text);
        Assert.Contains("inferred", text);
        Assert.Contains("segredo", text);
        Assert.Contains("dados pessoais de alunos", text);
        Assert.Contains("nao ve a conversa", text);
        Assert.Contains("cliente", text);
    }
}
