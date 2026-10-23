using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class SaveAssignmentGradeCommandHandlerTests
{
    [Fact]
    public async Task Handle_EnviaNotaIndividualParaGateway()
    {
        var gateway = new FakeMoodleAssignmentGradingGateway();
        var sut = CreateHandler(gateway);

        var result = await sut.Handle(
            new SaveAssignmentGradeCommand(
                "321",
                "501",
                "101",
                8.5m,
                "Bom trabalho. Revise apenas a conclusao.",
                AttemptNumber: -1,
                AddAttempt: false,
                ApplyToAll: false,
                WorkflowState: string.Empty,
                CourseId: "10"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(gateway.LastRequest);
        Assert.Equal("321", gateway.LastUserExternalId);
        Assert.Equal("501", gateway.LastRequest!.AssignmentId);
        Assert.Equal("101", gateway.LastRequest.StudentId);
        Assert.Equal(8.5m, gateway.LastRequest.Grade);
        Assert.Equal("Bom trabalho. Revise apenas a conclusao.", gateway.LastRequest.FeedbackText);
        Assert.Equal("mod_assign_save_grade", result.MoodleFunction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_RejeitaIdentificadorDeTarefaVazio(string assignmentId)
    {
        var gateway = new FakeMoodleAssignmentGradingGateway();
        var sut = CreateHandler(gateway);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.Handle(
                new SaveAssignmentGradeCommand(
                    "321",
                    assignmentId,
                    "101",
                    8.5m,
                    "Feedback revisado.",
                    AttemptNumber: -1,
                    AddAttempt: false,
                    ApplyToAll: false,
                    WorkflowState: string.Empty),
                CancellationToken.None));

        Assert.Equal("assignmentId", ex.ParamName);
        Assert.Null(gateway.LastRequest);
    }

    [Fact]
    public async Task Handle_RejeitaNotaNegativa()
    {
        var gateway = new FakeMoodleAssignmentGradingGateway();
        var sut = CreateHandler(gateway);

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.Handle(
                new SaveAssignmentGradeCommand(
                    "321",
                    "501",
                    "101",
                    -1m,
                    "Feedback revisado.",
                    AttemptNumber: -1,
                    AddAttempt: false,
                    ApplyToAll: false,
                    WorkflowState: string.Empty),
                CancellationToken.None));

        Assert.Equal("grade", ex.ParamName);
        Assert.Null(gateway.LastRequest);
    }

    [Fact]
    public async Task Handle_BloqueiaQuandoFlagDeNotaEstaDesabilitada()
    {
        var gateway = new FakeMoodleAssignmentGradingGateway();
        var sut = CreateHandler(
            gateway,
            assignmentGradeWriteEnabled: false,
            assignmentFeedbackWriteEnabled: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(
                new SaveAssignmentGradeCommand(
                    "321",
                    "501",
                    "101",
                    8.5m,
                    "Feedback revisado.",
                    AttemptNumber: -1,
                    AddAttempt: false,
                    ApplyToAll: false,
                    WorkflowState: string.Empty),
                CancellationToken.None));

        Assert.Equal("A escrita de notas em tarefas esta desabilitada por feature flag.", ex.Message);
        Assert.Null(gateway.LastRequest);
    }

    [Fact]
    public async Task Handle_PermiteSomenteFeedbackEmAtividadeSemNotaNumerica()
    {
        var gateway = new FakeMoodleAssignmentGradingGateway();
        var sut = CreateHandler(
            gateway,
            assignmentGradeWriteEnabled: false,
            assignmentFeedbackWriteEnabled: true,
            maxGrade: 0m);

        var result = await sut.Handle(
            new SaveAssignmentGradeCommand(
                UserExternalId: "321",
                AssignmentId: "501",
                StudentId: "101",
                Grade: null,
                FeedbackText: "Feedback sem nota.",
                AttemptNumber: -1,
                AddAttempt: false,
                ApplyToAll: false,
                WorkflowState: string.Empty,
                CourseId: "10"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(gateway.LastRequest);
        Assert.Null(gateway.LastRequest!.Grade);
        Assert.Equal("Feedback sem nota.", gateway.LastRequest.FeedbackText);
    }

    [Fact]
    public async Task Handle_BloqueiaNotaNulaEmAtividadeNumericaParaEvitarRemoverNotaExistente()
    {
        var gateway = new FakeMoodleAssignmentGradingGateway();
        var sut = CreateHandler(gateway, maxGrade: 10m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(
                new SaveAssignmentGradeCommand(
                    UserExternalId: "321",
                    AssignmentId: "501",
                    StudentId: "101",
                    Grade: null,
                    FeedbackText: "Somente o feedback mudou.",
                    AttemptNumber: 0,
                    AddAttempt: false,
                    ApplyToAll: false,
                    WorkflowState: string.Empty,
                    CourseId: "10"),
                CancellationToken.None));

        Assert.Contains("preserve explicitamente", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(gateway.LastRequest);
    }

    [Fact]
    public async Task Handle_PermitePreservarAusenciaDeNotaEmAtividadeNumericaSomenteComSinalInternoDePreflight()
    {
        var gateway = new FakeMoodleAssignmentGradingGateway();
        var sut = CreateHandler(gateway, maxGrade: 10m);

        var result = await sut.Handle(
            new SaveAssignmentGradeCommand(
                UserExternalId: "321",
                AssignmentId: "501",
                StudentId: "101",
                Grade: null,
                FeedbackText: "Feedback sem nota atribuida.",
                AttemptNumber: 0,
                AddAttempt: false,
                ApplyToAll: false,
                WorkflowState: string.Empty,
                CourseId: "10",
                PreserveGrade: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(gateway.LastRequest!.Grade);
    }

    [Fact]
    public async Task Handle_RejeitaNotaAcimaDaEscalaConfirmada()
    {
        var gateway = new FakeMoodleAssignmentGradingGateway();
        var sut = CreateHandler(gateway);

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.Handle(
                new SaveAssignmentGradeCommand(
                    "321", "501", "101", 11m, "Feedback.", -1, false, false, string.Empty, "10"),
                CancellationToken.None));

        Assert.Equal("grade", ex.ParamName);
        Assert.Null(gateway.LastRequest);
    }

    [Fact]
    public async Task Handle_BloqueiaQuandoEscalaNaoPodeSerConfirmada()
    {
        var gateway = new FakeMoodleAssignmentGradingGateway();
        var sut = CreateHandler(gateway, maxGrade: 0m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(
                new SaveAssignmentGradeCommand(
                    "321", "501", "101", 8m, "Feedback.", -1, false, false, string.Empty, "10"),
                CancellationToken.None));

        Assert.Contains("escala maxima", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(gateway.LastRequest);
    }

    [Fact]
    public async Task Handle_BloqueiaFeedbackQuandoFlagDeFeedbackEstaDesabilitada()
    {
        var gateway = new FakeMoodleAssignmentGradingGateway();
        var sut = CreateHandler(
            gateway,
            assignmentGradeWriteEnabled: true,
            assignmentFeedbackWriteEnabled: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(
                new SaveAssignmentGradeCommand(
                    "321",
                    "501",
                    "101",
                    8.5m,
                    "Feedback revisado.",
                    AttemptNumber: -1,
                    AddAttempt: false,
                    ApplyToAll: false,
                    WorkflowState: string.Empty),
                CancellationToken.None));

        Assert.Equal("A escrita de feedback em tarefas esta desabilitada por feature flag.", ex.Message);
        Assert.Null(gateway.LastRequest);
    }

    private static SaveAssignmentGradeCommandHandler CreateHandler(
        FakeMoodleAssignmentGradingGateway gateway,
        bool assignmentGradeWriteEnabled = true,
        bool assignmentFeedbackWriteEnabled = true,
        decimal maxGrade = 10m)
    {
        return new SaveAssignmentGradeCommandHandler(
            gateway,
            Options.Create(new AssignmentWriteFeatureOptions
            {
                AssignmentGradeWriteEnabled = assignmentGradeWriteEnabled,
                AssignmentFeedbackWriteEnabled = assignmentFeedbackWriteEnabled
            }),
            new FakeMoodleAssignmentSettingsGateway(maxGrade));
    }

    private sealed class FakeMoodleAssignmentGradingGateway : IMoodleAssignmentGradingGateway
    {
        public string? LastUserExternalId { get; private set; }

        public AssignmentGradeWriteRequest? LastRequest { get; private set; }

        public Task<AssignmentGradeWriteResult> SaveGradeAsync(
            string userExternalId,
            AssignmentGradeWriteRequest request,
            CancellationToken cancellationToken)
        {
            LastUserExternalId = userExternalId;
            LastRequest = request;
            return Task.FromResult(new AssignmentGradeWriteResult(
                Success: true,
                MoodleFunction: "mod_assign_save_grade",
                MoodleStatus: "ok"));
        }
    }

    private sealed class FakeMoodleAssignmentSettingsGateway(decimal maxGrade) : IMoodleAssignmentSettingsGateway
    {
        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId,
            string courseId,
            string assignmentId,
            CancellationToken cancellationToken)
            => Task.FromResult<AssignmentSettingsSummary?>(new AssignmentSettingsSummary(
                assignmentId,
                maxGrade,
                IsGradable: maxGrade > 0m,
                RawGrade: maxGrade > 0m ? maxGrade : 0m,
                MarkingWorkflowEnabled: false));
    }
}
