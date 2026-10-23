namespace Moodle.Presentation.Configuration;

public interface IMcpToolExposurePolicy
{
    bool ShouldExpose(string toolName, MoodleToolMetadataAttribute? metadata);
}
