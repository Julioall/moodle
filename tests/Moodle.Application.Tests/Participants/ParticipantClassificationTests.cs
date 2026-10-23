using Moodle.Domain;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Participants;

public sealed class ParticipantClassificationTests
{
    [Fact]
    public void Classifica_papel_de_aluno()
    {
        var result = ParticipantClassification.Classify(CreateParticipant(
            new CourseParticipantRole("5", "student", "Estudante")));

        Assert.Equal(ParticipantClassificationKind.Student, result);
    }

    [Fact]
    public void Inclui_participante_sem_papel_por_fallback()
    {
        var result = ParticipantClassification.Classify(CreateParticipant());

        Assert.Equal(ParticipantClassificationKind.UncertainFallback, result);
    }

    [Fact]
    public void Nao_trata_role_customizada_como_equipe_confirmada()
    {
        var result = ParticipantClassification.Classify(CreateParticipant(
            new CourseParticipantRole("9", "customrole", "Papel local")));

        Assert.Equal(ParticipantClassificationKind.Unclassified, result);
    }

    [Theory]
    [InlineData("monitor_go", "Monitor - GO")]
    [InlineData("editingteacher-go", "Professor - GO")]
    [InlineData("teacher", "Professor")]
    [InlineData("professor", "Professor")]
    [InlineData("monitor", "Monitor")]
    [InlineData("tutor", "Tutor")]
    [InlineData("manager", "Gestor")]
    [InlineData("coordinator", "Coordenador")]
    public void Exclui_roles_nao_estudantis_confirmadas(string shortName, string name)
    {
        var result = ParticipantClassification.Classify(CreateParticipant(
            new CourseParticipantRole("9", shortName, name)));

        Assert.Equal(ParticipantClassificationKind.KnownStaff, result);
    }

    [Fact]
    public void Exclui_perfil_exclusivamente_de_equipe()
    {
        var result = ParticipantClassification.Classify(CreateParticipant(
            new CourseParticipantRole("3", "editingteacher", "Professor")));

        Assert.Equal(ParticipantClassificationKind.KnownStaff, result);
    }

    [Fact]
    public void Marca_papeis_estudante_e_equipe_simultaneos_como_conflito()
    {
        var result = ParticipantClassification.Classify(CreateParticipant(
            new CourseParticipantRole("5", "student", "Estudante"),
            new CourseParticipantRole("3", "teacher", "Professor")));

        Assert.Equal(ParticipantClassificationKind.Unclassified, result);
    }

    [Theory]
    [InlineData("student-go", "Estudante")]
    [InlineData("student_go", "Student")]
    [InlineData("custom", "Estudante - GO")]
    [InlineData("aluno-go", "Aluno GO")]
    public void Reconhece_alias_de_estudante_com_sufixo_institucional(string shortName, string name)
    {
        Assert.Equal(ParticipantClassificationKind.Student, ParticipantClassification.Classify(CreateParticipant(
            new CourseParticipantRole("5", shortName, name))));
        Assert.Equal(ParticipantClassificationKind.Unclassified, ParticipantClassification.Classify(CreateParticipant(
            new CourseParticipantRole("6", "assistant", "Assistente"))));
        Assert.Equal(ParticipantClassificationKind.Unclassified, ParticipantClassification.Classify(CreateParticipant(
            new CourseParticipantRole("7", "student_assistant", "Student assistant"))));
    }

    [Fact]
    public void Reconhece_alias_com_maiusculas_e_acentos()
    {
        var result = ParticipantClassification.Classify(CreateParticipant(
            new CourseParticipantRole("5", null, "ALÚNO")));

        Assert.Equal(ParticipantClassificationKind.Student, result);
    }

    private static CourseParticipantSummary CreateParticipant(params CourseParticipantRole[] roles)
    {
        return new CourseParticipantSummary(
            "123", "Pessoa Teste", null, false, null, null, null, roles, []);
    }
}
