using Moodle.Application.Grading;
using Moodle.Domain;

namespace Moodle.Application.Tests.Grading;

public sealed class AssignmentContextCandidateRankingTests
{
    [Fact]
    public void Select_PriorizaArquivoDaAtividadeMesmoQuandoOAssignNaoTemDescricao()
    {
        var section = new CourseSectionSummary(
            "section-5",
            5,
            "Postagem das Superações",
            null,
            true,
            4,
            false,
            [
                Resource("resource-01", "Atividade Extra - 1", "Atividade_EAD_01_Antes_da_Aula_01.docx"),
                Assign("assign-01", "118396", "Enviar - Atividade Extra - 01"),
                Resource("resource-02", "Atividade Extra - 2", "Atividade_EAD_02_Depois_da_Aula_01.docx"),
                Resource("sap-01", "SAP 1", "SAP - 01.pdf")
            ]);
        var contents = new CourseContentsSummary("33447", [], true, false, [section]);
        var assignment = section.Modules[1];

        var result = AssignmentContextCandidateRanking.Select(
            contents,
            section,
            assignment,
            maxCandidates: 3,
            includeCourseMaterials: false);

        var selected = Assert.Single(result);
        Assert.Equal("Atividade_EAD_01_Antes_da_Aula_01.docx", selected.File!.FileName);
        Assert.True(selected.StrongMatch);
        Assert.Contains("numero", selected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_ProcuraEmOutraSecaoQuandoOArquivoNaoEstaAoLadoDoAssign()
    {
        var assignmentSection = new CourseSectionSummary(
            "section-5",
            5,
            "Atividades",
            null,
            true,
            1,
            false,
            [Assign("assign-01", "118396", "Enviar - Atividade Extra - 01")]);
        var contextSection = new CourseSectionSummary(
            "section-1",
            1,
            "Material do professor",
            null,
            true,
            1,
            false,
            [Resource("resource-01", "Roteiro da Atividade Extra 01", "enunciado-extra-01.docx")]);
        var contents = new CourseContentsSummary("33447", [], true, false, [contextSection, assignmentSection]);

        var result = AssignmentContextCandidateRanking.Select(
            contents,
            assignmentSection,
            assignmentSection.Modules[0],
            maxCandidates: 3,
            includeCourseMaterials: false);

        Assert.Equal("enunciado-extra-01.docx", Assert.Single(result).File!.FileName);
    }

    [Fact]
    public void Select_NaoConfundeNumeroIgualDeOutraFamiliaOuFolhaResposta()
    {
        var section = new CourseSectionSummary(
            "section-5",
            5,
            "Atividades",
            null,
            true,
            4,
            false,
            [
                Resource("sap-01", "SAP 1", "SAP - 01.pdf"),
                Resource("answer-01", "Folha Resposta - SAP 1", "Folha Resposta - SAP 01.odt"),
                Resource("extra-01", "Atividade Extra - 1", "Atividade_EAD_01_Antes_da_Aula_01.docx"),
                Assign("assign-01", "118396", "Enviar - Atividade Extra - 01")
            ]);
        var contents = new CourseContentsSummary("33447", [], true, false, [section]);

        var result = AssignmentContextCandidateRanking.Select(
            contents,
            section,
            section.Modules[3],
            maxCandidates: 3,
            includeCourseMaterials: true);

        var selected = Assert.Single(result);
        Assert.Equal("Atividade_EAD_01_Antes_da_Aula_01.docx", selected.File!.FileName);
    }

    [Fact]
    public void Select_PriorizaSapEspecificaSobreCronogramaGenerico()
    {
        var section = new CourseSectionSummary(
            "section-1",
            1,
            "Atividades",
            null,
            true,
            3,
            false,
            [
                Resource("sap-02", "SAP - 02", "SAP - 02.pdf"),
                Resource("calendar", "Cronograma do curso", "Cronograma.pdf"),
                Assign("assign-02", "118397", "SAP 2")
            ]);
        var contents = new CourseContentsSummary("33447", [], true, false, [section]);

        var result = AssignmentContextCandidateRanking.Select(
            contents,
            section,
            section.Modules[2],
            maxCandidates: 3,
            includeCourseMaterials: true);

        Assert.Equal("SAP - 02.pdf", result[0].File!.FileName);
        Assert.DoesNotContain(result, candidate =>
            candidate.File?.FileName?.Contains("Cronograma", StringComparison.OrdinalIgnoreCase) == true &&
            candidate.StrongMatch);
    }

    private static CourseModuleSummary Resource(string moduleId, string name, string fileName) =>
        new(
            moduleId,
            moduleId,
            "resource",
            name,
            null,
            true,
            true,
            null,
            null,
            [],
            [new CourseModuleFile("file", fileName, "/", 100, "application/octet-stream", $"https://example.test/{fileName}", false)]);

    private static CourseModuleSummary Assign(string moduleId, string instanceId, string name) =>
        new(moduleId, instanceId, "assign", name, null, true, true, null, null, [], []);
}
