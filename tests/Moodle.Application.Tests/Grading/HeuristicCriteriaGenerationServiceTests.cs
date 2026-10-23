using Moodle.Application.Abstractions;
using Moodle.Application.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class HeuristicCriteriaGenerationServiceTests
{
    private readonly HeuristicCriteriaGenerationService _sut = new();

    [Fact]
    public async Task GenerateAsync_ResultadosEsperadosEstruturados_ExtraiCriteriosLimpos()
    {
        var request = new CriteriaGenerationRequest(
            AssignmentName: "SAP 01 - Etapa 1",
            AssignmentDescription: null,
            ContextText:
                """
                Situação de Aprendizagem 01
                Resultados esperados: elaboração, em documento de texto, de um plano de gerenciamento de eventos, incidentes e problemas de TI;
                indicação das boas práticas de ITIL aplicáveis;
                apresentação de ações corretivas coerentes;
                descrição dos processos de escalonamento.
                Produto esperado: Plano de Gerenciamento.
                """,
            SupportingMaterials: null,
            MaxGrade: 49m);

        var result = await _sut.GenerateAsync(request, CancellationToken.None);

        Assert.Equal("model_generated_from_activity_context", result.Source);
        Assert.Equal(49m, result.MaxPoints);
        Assert.True(result.Criteria.Count >= 3, $"Esperava >= 3 critérios, obteve {result.Criteria.Count}");
        Assert.All(result.Criteria, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
            Assert.True(c.Description.Length >= 15, $"Critério muito curto: '{c.Description}'");
            Assert.True(c.MaxPoints > 0);
            Assert.NotNull(c.Id);
        });

        // Soma dos maxPoints deve ser igual ao maxGrade
        var totalPoints = result.Criteria.Sum(c => c.MaxPoints);
        Assert.Equal(49m, totalPoints);
    }

    [Fact]
    public async Task GenerateAsync_CriteriosAvaliacaoInline_ExtraiCriteriosLimpos()
    {
        var request = new CriteriaGenerationRequest(
            AssignmentName: "SAP 01 - Etapa 1",
            AssignmentDescription: null,
            ContextText:
                """
                Situação de Aprendizagem 01
                Envio SAP 01 - Etapa 1
                Valor da atividade: 49 pontos
                Critérios de avaliação: organização do plano de gerenciamento; descrição do gerenciamento de eventos, incidentes e problemas; aderência às boas práticas de ITIL; clareza na proposta de ações corretivas.
                Produto esperado: Plano de Gerenciamento de Eventos, Incidentes e Problemas de TI.
                """,
            SupportingMaterials: null,
            MaxGrade: 49m);

        var result = await _sut.GenerateAsync(request, CancellationToken.None);

        Assert.Equal("model_generated_from_activity_context", result.Source);
        Assert.Equal(49m, result.MaxPoints);
        Assert.True(result.Criteria.Count >= 4 && result.Criteria.Count <= 6,
            $"Esperava 4-6 critérios (4 originais + até 2 padrão), obteve {result.Criteria.Count}");
        Assert.Equal(49m, result.Criteria.Sum(c => c.MaxPoints));

        // Verificar que critérios contêm conteúdo relevante
        var descriptions = result.Criteria.Select(c => c.Description.ToLowerInvariant()).ToArray();
        Assert.Contains(descriptions, d => d.Contains("plano de gerenciamento") || d.Contains("organiza"));
        Assert.Contains(descriptions, d => d.Contains("gerenciamento de eventos") || d.Contains("descri"));
        Assert.Contains(descriptions, d => d.Contains("itil") || d.Contains("boas práticas") || d.Contains("boas praticas"));
        Assert.Contains(descriptions, d => d.Contains("ações corretivas") || d.Contains("acoes corretivas") || d.Contains("proposta"));
    }

    [Fact]
    public async Task GenerateAsync_TextoContaminadoComMetadados_FiltraMetadados()
    {
        var request = new CriteriaGenerationRequest(
            AssignmentName: "SAP 01",
            AssignmentDescription:
                "à distância - individual Resultados esperados: elaboração, em documento de texto, de um plano de gerenciamento de eventos, incidentes e problemas de TI.",
            ContextText: null,
            SupportingMaterials: null,
            MaxGrade: 49m);

        var result = await _sut.GenerateAsync(request, CancellationToken.None);

        // Nenhum critério deve conter "à distância - individual" como conteúdo principal
        Assert.All(result.Criteria, c =>
        {
            Assert.DoesNotContain("à distância - individual", c.Description);
        });

        // Deve ter extraído pelo menos um critério avaliável
        Assert.NotEmpty(result.Criteria);
    }

    [Fact]
    public async Task GenerateAsync_SomaMaxPointsBateComMaxGrade()
    {
        var request = new CriteriaGenerationRequest(
            AssignmentName: "SAP 02",
            AssignmentDescription: null,
            ContextText:
                """
                Resultados esperados: elaborar um plano de contingência;
                identificar os principais riscos operacionais;
                propor estratégias de mitigação;
                apresentar cronograma de implementação.
                """,
            SupportingMaterials: null,
            MaxGrade: 49m);

        var result = await _sut.GenerateAsync(request, CancellationToken.None);

        Assert.True(result.Criteria.Count >= 3);
        Assert.Equal(49m, result.Criteria.Sum(c => c.MaxPoints));
    }

    [Fact]
    public async Task GenerateAsync_CriteriosTruncadosRejeitados()
    {
        var request = new CriteriaGenerationRequest(
            AssignmentName: "SAP 03",
            AssignmentDescription: null,
            ContextText:
                """
                Resultados esperados: abc; de; elaborar um plano detalhado de gerenciamento de incidentes de TI.
                """,
            SupportingMaterials: null,
            MaxGrade: 20m);

        var result = await _sut.GenerateAsync(request, CancellationToken.None);

        // Critérios curtos "abc" e "de" devem ter sido rejeitados
        Assert.All(result.Criteria, c =>
        {
            Assert.True(c.Description.Length >= 15, $"Critério truncado não filtrado: '{c.Description}'");
        });
    }

    [Fact]
    public async Task GenerateAsync_ContextoInsuficiente_RetornaSemCriteriosComWarning()
    {
        var request = new CriteriaGenerationRequest(
            AssignmentName: "SAP 04",
            AssignmentDescription: null,
            ContextText: null,
            SupportingMaterials: null,
            MaxGrade: 10m);

        var result = await _sut.GenerateAsync(request, CancellationToken.None);

        Assert.Empty(result.Criteria);
        Assert.Equal(0m, result.Confidence);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task GenerateAsync_TransformaNominalizacaoEmVerbo()
    {
        var request = new CriteriaGenerationRequest(
            AssignmentName: "SAP 05",
            AssignmentDescription: null,
            ContextText:
                """
                Resultados esperados: elaboração, em documento de texto, de um plano de gerenciamento de riscos;
                identificação dos principais fatores de impacto operacional.
                """,
            SupportingMaterials: null,
            MaxGrade: 20m);

        var result = await _sut.GenerateAsync(request, CancellationToken.None);

        Assert.NotEmpty(result.Criteria);

        // Pelo menos um critério deve começar com verbo no infinitivo
        var hasVerbStart = result.Criteria.Any(c =>
            c.Description.StartsWith("Elaborar", StringComparison.OrdinalIgnoreCase) ||
            c.Description.StartsWith("Identificar", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasVerbStart, "Esperava que pelo menos um critério começasse com verbo no infinitivo.");
    }

    [Fact]
    public async Task GenerateAsync_ConfidenceMenorParaCriteriosInferidos()
    {
        var request = new CriteriaGenerationRequest(
            AssignmentName: "SAP 06",
            AssignmentDescription: null,
            ContextText:
                """
                O aluno deve elaborar um plano de continuidade de negócios.
                Deve considerar os principais riscos operacionais.
                """,
            SupportingMaterials: null,
            MaxGrade: 12m);

        var result = await _sut.GenerateAsync(request, CancellationToken.None);

        // Critérios inferidos sem header formal devem ter confidence < 1.0
        Assert.True(result.Confidence < 1m);
        Assert.True(result.Confidence > 0m);
    }

    [Fact]
    public async Task GenerateAsync_SemMaxGrade_ConfidenceReduzida()
    {
        var request = new CriteriaGenerationRequest(
            AssignmentName: "SAP 07",
            AssignmentDescription: null,
            ContextText:
                """
                Resultados esperados: elaborar um plano de gerenciamento;
                identificar riscos operacionais.
                """,
            SupportingMaterials: null,
            MaxGrade: 0m);

        var result = await _sut.GenerateAsync(request, CancellationToken.None);

        Assert.NotEmpty(result.Criteria);
        Assert.True(result.Confidence <= 0.3m, "Sem maxGrade, confiança deveria ser baixa.");
        Assert.Contains(result.Warnings, w => w.Contains("Nota máxima", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GenerateAsync_PrivateNotesInformaCriteriosGerados()
    {
        var request = new CriteriaGenerationRequest(
            AssignmentName: "SAP 08",
            AssignmentDescription: null,
            ContextText:
                """
                Resultados esperados: elaborar um plano de gerenciamento de eventos e incidentes de TI.
                """,
            SupportingMaterials: null,
            MaxGrade: 49m);

        var result = await _sut.GenerateAsync(request, CancellationToken.None);

        Assert.NotNull(result.PrivateNotesToTeacher);
        Assert.Contains("gerados a partir do contexto", result.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_CriteriosComVerbosAvaliaveis_ConfidenceMaior()
    {
        var request = new CriteriaGenerationRequest(
            AssignmentName: "SAP 09",
            AssignmentDescription: null,
            ContextText:
                """
                Resultados esperados: elaborar um plano de gerenciamento de eventos;
                identificar os principais incidentes e problemas de TI;
                apresentar ações corretivas coerentes;
                descrever os processos de escalonamento.
                """,
            SupportingMaterials: null,
            MaxGrade: 40m);

        var result = await _sut.GenerateAsync(request, CancellationToken.None);

        // Quando os critérios têm verbos avaliáveis, confiança deve ser >= 0.5
        Assert.True(result.Confidence >= 0.5m, $"Confiança deveria ser >= 0.5 com verbos avaliáveis, obteve {result.Confidence}");
    }
}
