using Moodle.Domain;

public sealed class AppCourseContractTests
{
    [Fact]
    public void CourseDto_preserves_composite_connection_reference_without_internal_entity_fields()
    {
        var dto = AppCourseContractMapper.ToDto(new CourseSummary("42", "n", "short", "Course", null, null, "Category", null, null, true, "https://moodle/course/42", null, null, null, null, null), "senai-go");
        Assert.Equal("senai-go", dto.ConnectionRef);
        Assert.Equal("42", dto.CourseId);
        Assert.DoesNotContain("Password", string.Join(',', dto.GetType().GetProperties().Select(p => p.Name)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivityDto_is_read_only_and_keeps_course_reference()
    {
        var dto = AppCourseContractMapper.ToDto(new CourseActivitySummary("7", "8", "assign", "Entrega", "https://moodle/mod/7", true, true, null, null, true, true, null, null, null, [], 0), "senai-go", "42");
        Assert.Equal(("senai-go", "42", "7"), (dto.ConnectionRef, dto.CourseId, dto.ActivityId));
        Assert.DoesNotContain(dto.GetType().GetProperties(), property => property.Name.Contains("Grade", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Feedback", StringComparison.OrdinalIgnoreCase));
    }
}
