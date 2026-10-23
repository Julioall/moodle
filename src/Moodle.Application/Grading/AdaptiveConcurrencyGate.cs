namespace Moodle.Application.Grading;

/// <summary>
/// Gate de concorrência que permite alterar o número de slots sem cancelar
/// trabalho em andamento. Reduzir o alvo deixa os slots atuais terminarem;
/// aumentar o alvo libera os próximos consumidores imediatamente.
/// </summary>
internal sealed class AdaptiveConcurrencyGate
{
    private readonly object sync = new();
    private TaskCompletionSource<bool> changed = CreateSignal();
    private int target;
    private int active;

    public AdaptiveConcurrencyGate(int initialTarget)
    {
        target = Math.Max(1, initialTarget);
    }

    public int Target
    {
        get
        {
            lock (sync)
            {
                return target;
            }
        }
    }

    public int Active
    {
        get
        {
            lock (sync)
            {
                return active;
            }
        }
    }

    public void SetTarget(int newTarget)
    {
        TaskCompletionSource<bool>? signal;
        lock (sync)
        {
            newTarget = Math.Max(1, newTarget);
            if (newTarget == target)
            {
                return;
            }

            target = newTarget;
            signal = changed;
            changed = CreateSignal();
        }

        signal.TrySetResult(true);
    }

    public async Task EnterAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (sync)
            {
                if (active < target)
                {
                    active++;
                    return;
                }

                waitTask = changed.Task;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }

    public void Exit()
    {
        TaskCompletionSource<bool>? signal = null;
        lock (sync)
        {
            if (active > 0)
            {
                active--;
                signal = changed;
                changed = CreateSignal();
            }
        }

        signal?.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
