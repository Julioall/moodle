using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;

namespace Moodle.Application.Tests.MoodleApi;

public sealed class MoodleReadRetryTests
{
    [Fact]
    public async Task Repete_leitura_transitoria_e_retorna_quando_a_proxima_tentativa_funciona()
    {
        var calls = 0;
        var retries = 0;

        var result = await MoodleReadRetry.ExecuteAsync(
            _ =>
            {
                calls++;
                if (calls < 3)
                {
                    throw new HttpRequestException("transient");
                }

                return Task.FromResult("ok");
            },
            (_, _) => retries++,
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
        Assert.Equal(2, retries);
    }

    [Fact]
    public async Task Helpers_aninhados_compartilham_o_orcamento_da_leitura_logica()
    {
        var outerCalls = 0;
        var innerCalls = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => MoodleReadRetry.ExecuteAsync(
            async cancellationToken =>
            {
                outerCalls++;
                await MoodleReadRetry.ExecuteAsync<string>(
                    _ =>
                    {
                        innerCalls++;
                        throw new HttpRequestException("transient");
                    },
                    onRetry: null,
                    cancellationToken);
                return "unreachable";
            },
            onRetry: null,
            CancellationToken.None));

        Assert.Equal(3, outerCalls);
        Assert.Equal(3, innerCalls);
        Assert.False(MoodleReadRetry.IsActive);
    }

    [Fact]
    public async Task Nao_repete_erro_de_permissao()
    {
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await MoodleReadRetry.ExecuteAsync<string>(
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException("permission denied");
                },
                null,
                CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Preserva_ultimo_erro_transitorio_quando_todas_as_tentativas_falham()
    {
        var calls = 0;
        var expected = new HttpRequestException("transport down");

        var actual = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await MoodleReadRetry.ExecuteAsync<string>(
                _ =>
                {
                    calls++;
                    throw expected;
                },
                null,
                CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Reutiliza_um_orcamento_por_consulta_distinta_em_um_lote_repetido()
    {
        var logicalAttempts = 0;
        MoodleAttemptBudget? firstPageBudget = null;
        MoodleAttemptBudget? secondPageBudget = null;

        var result = await MoodleReadRetry.ExecuteAsync(
            _ =>
            {
                logicalAttempts++;
                var pageOne = MoodleReadAttemptBudgetScope.GetBudget("page-one", maximumAttempts: 3)!;
                var pageTwo = MoodleReadAttemptBudgetScope.GetBudget("page-two", maximumAttempts: 3)!;
                firstPageBudget ??= pageOne;
                secondPageBudget ??= pageTwo;

                Assert.Same(firstPageBudget, pageOne);
                Assert.Same(secondPageBudget, pageTwo);
                Assert.NotSame(pageOne, pageTwo);
                Assert.True(pageOne.TryConsumeAttempt());
                Assert.True(pageTwo.TryConsumeAttempt());

                return logicalAttempts < 3
                    ? Task.FromException<string>(new HttpRequestException("transient"))
                    : Task.FromResult("ok");
            },
            onRetry: null,
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(3, logicalAttempts);
        Assert.Equal(3, firstPageBudget!.AttemptsUsed);
        Assert.Equal(3, secondPageBudget!.AttemptsUsed);
        Assert.Null(MoodleReadAttemptBudgetScope.GetBudget("outside-scope", maximumAttempts: 3));
    }

    [Fact]
    public async Task Mantem_o_orcamento_do_retry_interno_ao_reiniciar_o_retry_externo()
    {
        var outerAttempts = 0;
        var innerAttempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => MoodleReadRetry.ExecuteAsync(
            async _ =>
            {
                outerAttempts++;
                await MoodleReadRetry.ExecuteAsync<string>(
                    _ =>
                    {
                        innerAttempts++;
                        var budget = MoodleReadAttemptBudgetScope.GetBudget("same-read", maximumAttempts: 3)!;
                        if (!budget.TryConsumeAttempt())
                            throw new InvalidOperationException("shared attempt budget exhausted");
                        throw new HttpRequestException("transient");
                    },
                    onRetry: null,
                    CancellationToken.None);
                return true;
            },
            onRetry: null,
            CancellationToken.None));

        Assert.Equal(2, outerAttempts);
        Assert.Equal(4, innerAttempts);
    }
}
