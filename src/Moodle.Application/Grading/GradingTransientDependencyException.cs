namespace Moodle.Application.Grading;

/// <summary>
/// Sinaliza que a correção deve voltar para a fila depois de uma leitura
/// externa temporariamente indisponível. Isso evita transformar uma falha de
/// transporte em ausência de entrega ou em bloqueio pedagógico permanente.
/// </summary>
public sealed class GradingTransientDependencyException : Exception
{
    public GradingTransientDependencyException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "transient_dependency" : errorCode.Trim();
    }

    public string ErrorCode { get; }
}
