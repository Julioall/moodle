using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure.MoodleApi;

internal interface IMoodleDiagnosticStdoutWriter
{
    bool TryWrite(MoodleCallDiagnosticRecord record);
}

internal sealed class MoodleDiagnosticStdoutWriter : BackgroundService, IMoodleDiagnosticStdoutWriter
{
    internal const int QueueCapacity = 512;

    private readonly Channel<MoodleCallDiagnosticRecord> _records;
    private readonly IMoodleCallTelemetry _telemetry;
    private readonly TextWriter _output;

    public MoodleDiagnosticStdoutWriter(
        IMoodleCallTelemetry telemetry,
        TextWriter? output = null,
        int queueCapacity = QueueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        _telemetry = telemetry;
        _output = output ?? Console.Out;
        _records = Channel.CreateBounded<MoodleCallDiagnosticRecord>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public bool TryWrite(MoodleCallDiagnosticRecord record) => _records.Writer.TryWrite(record);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var record in _records.Reader.ReadAllAsync())
            {
                try
                {
                    await _output.WriteLineAsync(JsonSerializer.Serialize(record));
                }
                catch (Exception)
                {
                    RecordDrop();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _records.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    internal void RecordDrop()
    {
        try { _telemetry.RecordMoodleDiagnosticStdoutDrop(1); }
        catch (Exception) { }
    }
}
