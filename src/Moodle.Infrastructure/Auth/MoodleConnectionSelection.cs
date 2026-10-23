using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

internal sealed class MoodleConnectionSelection : IMoodleConnectionSelection
{
    public string? Alias { get; set; }
}
