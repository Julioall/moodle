using Moodle.Application.MoodleApi;

namespace Moodle.Application.Tests.MoodleApi;

public sealed class MoodleFunctionClassifierTests
{
    [Theory]
    [InlineData("mod_forum_get_forum_discussions_paginated")]
    [InlineData("mod_assign_get_submissions")]
    [InlineData("mod_assign_get_grades")]
    [InlineData("core_enrol_get_users_courses")]
    [InlineData("core_enrol_get_enrolled_users")]
    [InlineData("local_attendance_get_sessions")]
    public void Classify_ReturnsReadForDiscoveredQueryVerbs(string functionName)
    {
        Assert.Equal(MoodleFunctionRisk.Read, MoodleFunctionClassifier.Classify(functionName));
    }

    [Theory]
    [InlineData("core_course_delete_courses")]
    [InlineData("local_plugin_unknown_function")]
    public void Classify_RoutesNonQueriesThroughConfirmedWrite(string functionName)
    {
        Assert.Equal(MoodleFunctionRisk.ControlledWrite, MoodleFunctionClassifier.Classify(functionName));
    }
}
