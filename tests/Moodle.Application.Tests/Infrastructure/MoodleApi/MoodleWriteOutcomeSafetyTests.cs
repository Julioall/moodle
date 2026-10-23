using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleWriteOutcomeSafetyTests
{
    [Fact]
    public void Unknown_write_outcome_cannot_be_retried_or_requeued_directly()
    {
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8.5m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final revisado.", "teacher-1", 321);
        item.MarkCommitExecutionUnknown("A resposta foi perdida depois do envio.");

        Assert.False(item.ScheduleCommitRetry(
            "Ainda sem confirmacao remota.",
            "moodle_write_reconciled_not_applied",
            DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(item.RequeueCommitForRetry);
        Assert.Throws<InvalidOperationException>(() => item.RequeueForProcessing("Reprocessar item."));
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, item.CommitStatus);
    }
}
