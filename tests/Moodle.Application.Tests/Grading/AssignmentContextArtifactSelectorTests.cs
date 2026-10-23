using Moodle.Application.Grading;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class AssignmentContextArtifactSelectorTests
{
    [Fact]
    public async Task SelectAsync_PriorizaSapEspecificaSobreCronogramaMesmoComTextoMaior()
    {
        var now = DateTimeOffset.UtcNow;
        var sap = new GradingArtifact(
            Guid.NewGuid(), Guid.NewGuid(), "assignment_context", "SAP - 02.pdf", "application/pdf", null, 10,
            "succeeded", "Enunciado específico da SAP 2.", null, now);
        var cronograma = new GradingArtifact(
            Guid.NewGuid(), sap.GradingItemId, "assignment_context", "Cronograma.pdf", "application/pdf", null, 20,
            "succeeded", new string('x', 5000), null, now);

        var result = await AssignmentContextArtifactSelector.SelectAsync(
            [sap, cronograma],
            courseId: "33447",
            assignmentId: "118401",
            assignmentName: "SAP 2",
            new HeuristicAssignmentContextSelectionService(),
            CancellationToken.None);

        Assert.Same(sap, result.Artifact);
    }
}
