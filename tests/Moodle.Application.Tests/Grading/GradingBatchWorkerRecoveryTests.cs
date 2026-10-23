using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingBatchWorkerRecoveryTests
{
    [Fact]
    public void ManualRequeue_ReturnsToPreparationInsteadOfSendingItemDirectlyToAi()
    {
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.BlockAnalysis("A leitura do Moodle falhou antes da ingestão.");

        item.RequeueForProcessing("Tentativa manual autorizada.");

        Assert.Equal(GradingItemStatus.Pending, item.Status);
        Assert.Equal(GradingProcessingStage.Pending, item.ProcessingStage);
        Assert.Equal(GradingCommitStatus.NotReady, item.CommitStatus);
        Assert.Equal("Tentativa manual autorizada.", item.PrivateNotesToTeacher);
    }

    [Fact]
    public void ManualRequeue_DoesNotReopenFinalDecisionOrUnknownPublication()
    {
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 2);
        var reviewed = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        reviewed.SetDraft(8m, 0.8m, "Rascunho.");
        reviewed.ApplyTeacherReview(8m, "Decisão final.", "teacher-1", 321);

        Assert.Throws<InvalidOperationException>(() => reviewed.RequeueForProcessing("reabrir"));

        var unknown = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 0);
        unknown.SetDraft(7m, 0.8m, "Rascunho.");
        unknown.ApplyTeacherReview(7m, "Decisão final.", "teacher-1", 321);
        unknown.MarkCommitExecutionUnknown("Resposta perdida.");

        Assert.Throws<InvalidOperationException>(() => unknown.RequeueForProcessing("reabrir"));
    }
}
