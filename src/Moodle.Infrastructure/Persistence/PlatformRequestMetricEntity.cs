namespace Moodle.Infrastructure;

/// <summary>
/// Registro técnico de uma requisição do portal ou MCP. Não contém corpo,
/// parâmetros de consulta, credenciais ou identificação do usuário.
/// </summary>
public sealed class PlatformRequestMetricEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset RecordedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Method { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public int StatusCode { get; init; }
    public long DurationMs { get; init; }
    public string? FailureKind { get; init; }
}
