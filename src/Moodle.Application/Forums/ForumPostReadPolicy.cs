using Microsoft.Extensions.Options;
using Moodle.Application.Configuration;

namespace Moodle.Application.Forums;

internal static class ForumPostReadPolicy
{
    public static int GetMaxConcurrentPosts(IOptions<MoodleReadLimitsOptions>? readLimits)
    {
        if (readLimits is null)
        {
            return 1;
        }

        var options = readLimits.Value;
        return Math.Clamp(
            Math.Min(options.MaxConcurrentForumPostReads, options.MaxConcurrentPerConnection),
            1,
            4);
    }
}
