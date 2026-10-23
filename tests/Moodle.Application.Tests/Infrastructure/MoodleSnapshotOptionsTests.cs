using Moodle.Application.Abstractions;
using Moodle.Infrastructure.Configuration;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleSnapshotOptionsTests
{
    [Fact]
    public void Normalize_ProvidesHeadroomForLargeCourseCatalogues()
    {
        var options = new MoodleSnapshotOptions().Normalize();

        Assert.Equal(250, options.CoursePageSize);
        Assert.Equal(100, options.MaxCoursePages);
        Assert.Equal(25_000, options.CoursePageSize * options.MaxCoursePages);
    }

    [Fact]
    public void GetPayloadLimit_UsesCourseSpecificCapAndPreservesOtherDatasetCap()
    {
        var options = new MoodleSnapshotOptions().Normalize();
        var projectedCoursePayload = 12 * 1024 * 1024;
        var overCourseLimit = 33 * 1024 * 1024;

        Assert.Equal(32 * 1024 * 1024, options.GetPayloadLimit(MoodleSnapshotDatasets.Courses));
        Assert.True(projectedCoursePayload <= options.GetPayloadLimit(MoodleSnapshotDatasets.Courses));
        Assert.True(overCourseLimit > options.GetPayloadLimit(MoodleSnapshotDatasets.Courses));
        Assert.Equal(10 * 1024 * 1024, options.GetPayloadLimit(MoodleSnapshotDatasets.Activities));
    }
}
