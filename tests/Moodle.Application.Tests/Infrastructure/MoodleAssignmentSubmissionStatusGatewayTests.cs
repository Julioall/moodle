using System.Text.Json;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAssignmentSubmissionStatusGatewayTests
{
    [Fact]
    public void HasExistingFeedback_ReconheceFeedbackDataDoContratoMoodle()
    {
        using var document = JsonDocument.Parse("""
        {
          "lastattempt": { "submission": { "status": "submitted" } },
          "feedback": {
            "gradefordisplay": "8/10",
            "feedbackdata": [
              {
                "name": "comments",
                "editorfields": [{ "name": "comment", "text": "Feedback sintético", "format": 1 }]
              }
            ]
          }
        }
        """);

        Assert.True(MoodleAssignmentSubmissionStatusGateway.HasExistingFeedback(document.RootElement));

        var status = MoodleAssignmentSubmissionStatusGateway.ParseStatus(document.RootElement.GetRawText(), 1095254, 123);
        Assert.NotNull(status);
        Assert.True(status.HasFeedback);
    }

    [Fact]
    public void HasExistingFeedback_ReconheceArquivoDeFeedback()
    {
        using var document = JsonDocument.Parse("""
        {
          "feedback": {
            "feedbackdata": [
              {
                "name": "files",
                "fileareas": [{ "area": "feedback_files", "files": [{ "filename": "retorno-sintetico.pdf" }] }]
              }
            ]
          }
        }
        """);

        Assert.True(MoodleAssignmentSubmissionStatusGateway.HasExistingFeedback(document.RootElement));
    }

    [Fact]
    public void HasExistingFeedback_MantemFormatoDiretoLegado()
    {
        using var document = JsonDocument.Parse("""
        {
          "feedbackplugins": [
            {
              "type": "comments",
              "editorfields": [{ "name": "comment", "text": "Feedback legado sintético", "format": 1 }]
            }
          ]
        }
        """);

        Assert.True(MoodleAssignmentSubmissionStatusGateway.HasExistingFeedback(document.RootElement));
    }

    [Fact]
    public void HasExistingFeedback_NaoConfundePluginDaEntregaComFeedback()
    {
        using var document = JsonDocument.Parse("""
        {
          "lastattempt": {
            "submission": {
              "status": "submitted",
              "plugins": [{ "type": "onlinetext", "editorfields": [{ "text": "Resposta do aluno" }] }]
            }
          }
        }
        """);

        Assert.False(MoodleAssignmentSubmissionStatusGateway.HasExistingFeedback(document.RootElement));
    }
}
