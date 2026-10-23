using Moodle.Application.Abstractions;
using Moodle.Application.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingAnalysisServiceTests
{
    private readonly StructuredGradingAnalysisService _sut = new();

    [Fact]
    public async Task AnalyzeAsync_SubmissaoVazia_RetornaBlockedEmptySubmission()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 01",
            MaxGrade: 10m,
            ActivityDescription: "Descricao da atividade.",
            RubricOrCriteria: "Criterio A; Criterio B",
            TeacherInstructions: null,
            SubmissionText: "",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AnalysisStatus.BlockedEmptySubmission, result.AnalysisStatus);
        Assert.Null(result.SuggestedGrade);
        Assert.Equal(0m, result.Confidence);
        Assert.NotEmpty(result.Blocks);
    }

    [Fact]
    public async Task AnalyzeAsync_SemCriteriosNemDescricao_RetornaAwaitingAiAnalysis()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 01",
            MaxGrade: 10m,
            ActivityDescription: null,
            RubricOrCriteria: null,
            TeacherInstructions: null,
            SubmissionText: "O estudante respondeu a atividade com um texto abrangente.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.Null(result.SuggestedGrade);
        Assert.Null(result.FeedbackToStudent);
        Assert.Empty(result.CriterionAnalysis);
        Assert.Empty(result.Blocks);
        Assert.Contains("Pre-validacao concluida", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_SemMaxGrade_RetornaAwaitingAiComDiagnostico()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 01",
            MaxGrade: 0m,
            ActivityDescription: null,
            RubricOrCriteria: "Identifica riscos fisicos no ambiente industrial",
            TeacherInstructions: null,
            SubmissionText: "Resposta do estudante sobre riscos fisicos.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.Null(result.SuggestedGrade);
        Assert.Null(result.FeedbackToStudent);
        Assert.Empty(result.CriterionAnalysis);
        Assert.Contains("Nota maxima nao identificada", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Blocks);
    }

    [Fact]
    public async Task AnalyzeAsync_SubmissaoComCriterios_RetornaAwaitingAiSemNotaSugerida()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 01",
            MaxGrade: 10m,
            ActivityDescription: "Analise de riscos em ambiente industrial.",
            RubricOrCriteria: "Identifica riscos fisicos no ambiente; Descreve medidas preventivas adequadas; Utiliza normas tecnicas como referencia",
            TeacherInstructions: "Linguagem acolhedora.",
            SubmissionText: "O estudante identificou os principais riscos fisicos no ambiente industrial. " +
                            "Foram descritas diversas medidas preventivas conforme as normas tecnicas vigentes. " +
                            "A abordagem foi clara e bem estruturada.",
            FileHashes: ["abc123", "def456"]);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        // IA-first: pré-validação diagnóstica, sem nota/feedback heurístico
        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.Null(result.SuggestedGrade);
        Assert.Null(result.FeedbackToStudent);
        Assert.Empty(result.CriterionAnalysis);
        Assert.Empty(result.Blocks);

        // Notas diagnósticas devem indicar contexto identificado
        Assert.NotEmpty(result.PrivateNotesToTeacher!);
        Assert.Contains("Pre-validacao concluida", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nota maxima: 10", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_CriteriosComBarraVertical_RetornaAwaitingAi()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 02",
            MaxGrade: 100m,
            ActivityDescription: null,
            RubricOrCriteria: "Identifica riscos fisicos no ambiente | Descreve medidas preventivas adequadas | Utiliza normas tecnicas como referencia | Propoe acoes corretivas coerentes",
            TeacherInstructions: null,
            SubmissionText: "Resposta com conteudo relevante sobre os criterios propostos.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        // IA-first: nenhum critério heurístico é gerado
        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.Empty(result.CriterionAnalysis);
        Assert.Null(result.SuggestedGrade);
        Assert.Null(result.FeedbackToStudent);
    }

    [Fact]
    public async Task AnalyzeAsync_SubmissaoSemCoberturaDeCriterios_RetornaAwaitingAi()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 03",
            MaxGrade: 10m,
            ActivityDescription: null,
            RubricOrCriteria: "Identificacao de riscos quimicos especificos",
            TeacherInstructions: null,
            SubmissionText: "O aluno fez uma entrega genérica sem mencionar nenhum aspecto especifico.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        // IA-first: sem critérios heurísticos, sem revisão de cobertura
        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.Empty(result.CriterionAnalysis);
        Assert.Null(result.FeedbackToStudent);
    }

    [Fact]
    public async Task AnalyzeAsync_TextoLegivelComCriterios_DiagnosticoContemInformacaoContextual()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 04",
            MaxGrade: 10m,
            ActivityDescription: null,
            RubricOrCriteria: "Descrever riscos fisicos; Apresentar medidas preventivas; Relacionar normas tecnicas",
            TeacherInstructions: null,
            SubmissionText: "Resposta curta.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.NotNull(result.PrivateNotesToTeacher);
        Assert.Contains("Pre-validacao concluida", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("palavras", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_ComDescricaoComoFallback_RetornaAwaitingAi()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 05",
            MaxGrade: 10m,
            ActivityDescription: "O aluno deve elaborar um plano de continuidade; considerar riscos operacionais; propor estratégias de mitigação. O aluno devera escrever uma redacao.",
            RubricOrCriteria: null,
            TeacherInstructions: null,
            SubmissionText: "O estudante elaborou um plano de continuidade abordando os riscos operacionais " +
                            "e propondo estrategias de mitigacao para garantir a resiliencia do negocio.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        // IA-first: não gera critérios nem nota
        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.Null(result.SuggestedGrade);
        Assert.Null(result.FeedbackToStudent);
        Assert.Empty(result.CriterionAnalysis);
        Assert.Empty(result.Blocks);
        // Diagnóstico indica contexto disponível
        Assert.Contains("Enunciado da atividade disponivel", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_ComDescricaoMuitoPequena_ContinuaParaIAComAviso()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 06",
            MaxGrade: 0m,
            ActivityDescription: "Atividade sobre riscos fisicos. Valor da atividade: 16 pontos.",
            RubricOrCriteria: null,
            TeacherInstructions: null,
            SubmissionText: "O estudante identificou riscos fisicos e propôs medidas preventivas adequadas ao contexto.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.Null(result.SuggestedGrade);
        Assert.Empty(result.Blocks);
        Assert.Contains("curto", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
    }

    // ========================
    // Validações do novo fluxo IA-first
    // ========================

    [Fact]
    public async Task AnalyzeAsync_CriteriosComMenosDe3PalavrasUteis_NaoGeramCriteriosHeuristicos()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 07",
            MaxGrade: 10m,
            ActivityDescription: null,
            RubricOrCriteria: "ITIL; Etapa 1; língua portuguesa; Identifica riscos fisicos no ambiente industrial",
            TeacherInstructions: null,
            SubmissionText: "O estudante identificou riscos fisicos relevantes no ambiente industrial.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        // IA-first: nenhum critério heurístico é retornado, independente da qualidade
        Assert.Empty(result.CriterionAnalysis);
        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
    }

    [Fact]
    public async Task AnalyzeAsync_FragmentosIsolados_NaoGeramCriterios()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 08",
            MaxGrade: 10m,
            ActivityDescription: null,
            RubricOrCriteria: "Etapa 1; ITIL; língua; avaliação",
            TeacherInstructions: null,
            SubmissionText: "O estudante apresentou um plano detalhado.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Empty(result.CriterionAnalysis);
    }

    [Fact]
    public async Task AnalyzeAsync_NuncaGeraFeedbackHeuristico()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 09",
            MaxGrade: 10m,
            ActivityDescription: null,
            RubricOrCriteria: "Identifica riscos fisicos no ambiente; Descreve medidas preventivas adequadas; Utiliza normas tecnicas como referencia",
            TeacherInstructions: null,
            SubmissionText: "O estudante identificou riscos fisicos e descreveu medidas preventivas usando normas tecnicas.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        // IA-first: feedback é sempre null (será gerado pela IA)
        Assert.Null(result.FeedbackToStudent);
        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
    }

    [Fact]
    public async Task AnalyzeAsync_NuncaGeraNotaSugeridaHeuristica()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 10",
            MaxGrade: 10m,
            ActivityDescription: null,
            RubricOrCriteria: "Identifica riscos fisicos no ambiente industrial; Elabora plano de contingencia estruturado; Apresenta cronograma de implementacao detalhado",
            TeacherInstructions: null,
            SubmissionText: "O aluno identificou riscos fisicos basicos.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Null(result.SuggestedGrade);
        Assert.Null(result.FeedbackToStudent);
        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
    }

    [Fact]
    public async Task AnalyzeAsync_RetornaDiagnosticoComInformacaoDeEscala()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 11",
            MaxGrade: 10m,
            ActivityDescription: null,
            RubricOrCriteria: "Identifica riscos fisicos no ambiente; Descreve medidas preventivas adequadas; Utiliza normas tecnicas como referencia",
            TeacherInstructions: null,
            SubmissionText: "O estudante identificou riscos fisicos e propôs medidas preventivas baseadas em normas tecnicas.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.Contains("Nota maxima: 10", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prepare_ai_grading_batch", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_DiagnosticoIdentificaCriteriosFormais()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 12",
            MaxGrade: 10m,
            ActivityDescription: null,
            RubricOrCriteria: "Elabora plano de gerenciamento de eventos de TI; Identifica incidentes e problemas criticos",
            TeacherInstructions: null,
            SubmissionText: "O plano de gerenciamento apresenta eventos de TI e descreve incidentes criticos com problemas de infraestrutura.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.Contains("Criterios formais", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_DiagnosticoSemCriterios_IndicaAusencia()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "Envio SAP 01 - Etapa 1",
            MaxGrade: 49m,
            ActivityDescription: null,
            RubricOrCriteria: null,
            TeacherInstructions: null,
            SubmissionText: "O plano de gerenciamento de eventos de TI apresenta os principais eventos " +
                            "que podem impactar a operacao da empresa, incluindo falhas de hardware, " +
                            "problemas de rede e incidentes de seguranca. O plano segue as orientacoes da ITIL " +
                            "para monitoramento e tratamento de eventos.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.Null(result.FeedbackToStudent);
        Assert.Null(result.SuggestedGrade);
        Assert.Contains("Nenhum criterio", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_NotaBaixaOuAlta_NaoGeraFeedbackHeuristico()
    {
        // Cenário com nota baixa — verificar que não gera feedback heurístico
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 13",
            MaxGrade: 100m,
            ActivityDescription: null,
            RubricOrCriteria: "Elaborar plano de gerenciamento de eventos de TI; Indicar eventos que impactam operacao; Relacionar com ITIL; Organizar texto com norma culta",
            TeacherInstructions: null,
            SubmissionText: "Entrega breve sem muito conteudo.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Null(result.FeedbackToStudent);
        Assert.Null(result.SuggestedGrade);
        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
    }

    [Fact]
    public async Task AnalyzeAsync_ComCriteriosEMaxGrade_DiagnosticoCompleto()
    {
        var request = new GradingAnalysisRequest(
            AssignmentName: "SA 14",
            MaxGrade: 49m,
            ActivityDescription: null,
            RubricOrCriteria: "Elaborar plano de gerenciamento de eventos de TI; Indicar eventos que impactam operacao; Relacionar com ITIL; Organizar texto com norma culta",
            TeacherInstructions: null,
            SubmissionText: "O estudante elaborou um plano de gerenciamento de eventos de TI. " +
                            "O texto indica alguns eventos mas nao relaciona com ITIL.",
            FileHashes: []);

        var result = await _sut.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(AnalysisStatus.AwaitingAiAnalysis, result.AnalysisStatus);
        Assert.Null(result.FeedbackToStudent);
        Assert.Null(result.SuggestedGrade);
        Assert.Empty(result.CriterionAnalysis);
        Assert.Contains("Pre-validacao concluida", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("49", result.PrivateNotesToTeacher);
    }
}
