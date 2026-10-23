using Moodle.Application.Abstractions;
using Moodle.Application.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class HeuristicAssignmentContextSelectionServiceTests
{
    [Fact]
    public async Task SelectAsync_PriorizaDocumentoComSapEtapaEOrientacoesDaTarefa()
    {
        var sut = new HeuristicAssignmentContextSelectionService();

        var result = await sut.SelectAsync(
            new AssignmentContextSelectionRequest(
                CourseId: "29972",
                AssignmentId: "101112",
                AssignmentName: "Envio SAP 01 - Etapa 1",
                AssignmentDescription: null,
                Candidates:
                [
                    new AssignmentContextCandidate(
                        "c1",
                        "resource",
                        "Calendario do curso",
                        "Datas gerais e avisos administrativos.",
                        SectionNumber: 1,
                        DistanceFromAssignment: 1),
                    new AssignmentContextCandidate(
                        "c2",
                        "resource",
                        "Orientacoes SAP 01 - Etapa 1",
                        "Enunciado da atividade SAP 01 etapa 1 com criterios de entrega e itens esperados.",
                        SectionNumber: 1,
                        DistanceFromAssignment: 0),
                    new AssignmentContextCandidate(
                        "c3",
                        "page",
                        "Material complementar ITIL",
                        "Texto de apoio para estudo, sem instrucoes de envio.",
                        SectionNumber: 1,
                        DistanceFromAssignment: 2)
                ]),
            CancellationToken.None);

        Assert.Equal("c2", result.SelectedCandidateId);
        Assert.Equal("assignment_statement", result.Classification);
        Assert.True(result.Confidence >= 0.7m);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task SelectAsync_SemCandidatosRetornaBaixaConfiancaEWarning()
    {
        var sut = new HeuristicAssignmentContextSelectionService();

        var result = await sut.SelectAsync(
            new AssignmentContextSelectionRequest(
                CourseId: "29972",
                AssignmentId: "101112",
                AssignmentName: "Envio SAP 01 - Etapa 1",
                AssignmentDescription: null,
                Candidates: []),
            CancellationToken.None);

        Assert.Null(result.SelectedCandidateId);
        Assert.Equal("none", result.Classification);
        Assert.Equal(0m, result.Confidence);
        Assert.Contains(result.Warnings, warning => warning.Contains("candidato", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SelectAsync_RejeitaDocumentoDeOutraAtividadeMesmoComTextoParecido()
    {
        var sut = new HeuristicAssignmentContextSelectionService();

        var result = await sut.SelectAsync(
            new AssignmentContextSelectionRequest(
                CourseId: "1",
                AssignmentId: "117499",
                AssignmentName: "Atividade EAD 04",
                AssignmentDescription: null,
                Candidates:
                [
                    new AssignmentContextCandidate(
                        "ead03",
                        "resource",
                        "03_Pre_Problema_antes_da_tecnologia.docx",
                        "Enunciado da Atividade EAD 03 com instruções de entrega.",
                        SectionNumber: 1,
                        DistanceFromAssignment: 0)
                ]),
            CancellationToken.None);

        Assert.Null(result.SelectedCandidateId);
        Assert.Equal("blocked", result.Classification);
        Assert.Contains(result.Warnings, warning => warning.Contains("outra atividade", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SelectAsync_PriorizaNumeroCorrespondenteQuandoHaCandidatosEad03EEad04()
    {
        var sut = new HeuristicAssignmentContextSelectionService();

        var result = await sut.SelectAsync(
            new AssignmentContextSelectionRequest(
                CourseId: "1",
                AssignmentId: "117499",
                AssignmentName: "Atividade EAD 03",
                AssignmentDescription: null,
                Candidates:
                [
                    new AssignmentContextCandidate("ead04", "resource", "04_Pre_Projeto.docx", "Enunciado EAD 04", 1, 0),
                    new AssignmentContextCandidate("ead03", "resource", "03_Pre_Problema_antes_da_tecnologia.docx", "Enunciado EAD 03", 1, 1)
                ]),
            CancellationToken.None);

        Assert.Equal("ead03", result.SelectedCandidateId);
    }
}
