using Moodle.Application.Abstractions;
using Moodle.Application.Gradebook.Queries;

namespace Moodle.Application.Tests.Gradebook.Queries;

public sealed class GetStudentGradebookQueryHandlerTests
{
    [Fact]
    public async Task Handle_DeveRetornarBoletimDoGateway()
    {
        var gateway = new FakeMoodleGradebookGateway();
        var sut = new GetStudentGradebookQueryHandler(gateway);

        var result = await sut.Handle(new GetStudentGradebookQuery("10", "123"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("10", result.CourseId);
        Assert.Equal("123", result.StudentId);
        Assert.Single(result.Items);
        Assert.Equal("Atividade Teste", result.Items.First().ItemName);
    }

    private sealed class FakeMoodleGradebookGateway : IMoodleGradebookGateway
    {
        public Task<CourseGradebook> GetStudentGradebookAsync(string courseId, string studentId, CancellationToken cancellationToken)
        {
            var items = new List<GradebookItem>
            {
                new GradebookItem(
                    Id: "1",
                    ItemName: "Atividade Teste",
                    ItemType: "mod",
                    ItemModule: "assign",
                    CategoryId: "2",
                    GradeRaw: 9,
                    GradeFormatted: "9,00",
                    GradeMin: 0,
                    GradeMax: 10,
                    PercentageFormatted: 90,
                    Feedback: "Muito bom",
                    FeedbackFormat: "1",
                    GradedDateSubmitted: 1600000000,
                    GradedDateGraded: 1600000100,
                    GraderId: "2")
            };

            return Task.FromResult(new CourseGradebook(courseId, studentId, items));
        }
    }
}
