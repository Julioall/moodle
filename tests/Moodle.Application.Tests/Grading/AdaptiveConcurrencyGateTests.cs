using Moodle.Application.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class AdaptiveConcurrencyGateTests
{
    [Fact]
    public async Task AumentarAlvoLiberaProximoSlotSemCancelarOAtual()
    {
        var gate = new AdaptiveConcurrencyGate(initialTarget: 1);
        await gate.EnterAsync(CancellationToken.None);

        var waiting = gate.EnterAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        gate.SetTarget(2);
        await waiting;

        Assert.Equal(2, gate.Active);
        gate.Exit();
        gate.Exit();
        Assert.Equal(0, gate.Active);
    }

    [Fact]
    public async Task ReduzirAlvoAguardaOsSlotsAtivosTerminarem()
    {
        var gate = new AdaptiveConcurrencyGate(initialTarget: 2);
        await gate.EnterAsync(CancellationToken.None);
        await gate.EnterAsync(CancellationToken.None);

        gate.SetTarget(1);
        Assert.Equal(2, gate.Active);

        var waiting = gate.EnterAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        gate.Exit();
        Assert.False(waiting.IsCompleted);
        gate.Exit();
        await waiting;
        Assert.Equal(1, gate.Active);
        gate.Exit();
    }
}
