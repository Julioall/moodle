using Moodle.Application.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleWriteScopePolicyTests
{
    [Theory]
    [InlineData("core_message_send_instant_messages", "moodle.write.messages")]
    [InlineData("core_message_send_messages_to_conversation", "moodle.write.messages")]
    [InlineData("mod_forum_add_discussion", "moodle.write.forums")]
    [InlineData("mod_forum_add_discussion_post", "moodle.write.forums")]
    [InlineData("core_calendar_create_calendar_events", "moodle.write.course_content")]
    [InlineData("mod_assign_save_grade", "moodle.write.assignments.grade")]
    [InlineData("mod_assign_save_grades", "moodle.write.assignments.grade")]
    public void ForFunction_RetornaSomenteEscopoExplicitamenteCadastrado(string functionName, string expectedScope)
    {
        Assert.True(MoodleWriteScopePolicy.TryGetScope(functionName, out var scope));
        Assert.Equal(expectedScope, scope);
        Assert.Equal(expectedScope, MoodleWriteScopePolicy.ForFunction(functionName));
    }

    [Theory]
    [InlineData("mod_assign_set_user_flags")]
    [InlineData("future_moodle_write_function")]
    [InlineData("")]
    public void ForFunction_UsaEscopoGenericoParaFuncaoSemFamiliaEspecializada(string functionName)
    {
        Assert.False(MoodleWriteScopePolicy.TryGetScope(functionName, out var scope));
        Assert.Empty(scope);

        if (string.IsNullOrWhiteSpace(functionName))
        {
            Assert.Throws<ArgumentException>(() => MoodleWriteScopePolicy.ForFunction(functionName));
            return;
        }

        Assert.Equal("moodle.write", MoodleWriteScopePolicy.ForFunction(functionName));
    }

    [Fact]
    public void TryGetScope_NaoAceitaNomeNulo()
    {
        Assert.False(MoodleWriteScopePolicy.TryGetScope(null, out var scope));
        Assert.Empty(scope);
    }
}
