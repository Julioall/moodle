namespace Moodle.Application.MoodleApi;

public sealed class MoodleResourceException(string errorCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;
}
