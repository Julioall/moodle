namespace Moodle.Application.MoodleApi;

public enum MoodleResourceType
{
    CourseId,
    CategoryId,
    CourseUrl,
    CategoryUrl,
    SearchUrl,
    IdNumber,
    ShortName,
    SearchTerm,
    Unknown
}

public sealed record MoodleResourceReference(
    MoodleResourceType Type,
    string OriginalInput,
    string Value);

public interface IMoodleResourceResolver
{
    MoodleResourceReference Resolve(string input);
}
