using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Presentation.Tools;
using Moodle.Presentation.Tools.Grading;
using Moodle.Presentation.Tools.Gradebook;
using Moodle.Presentation.Tools.Memory;
using Moodle.Presentation.Tools.Messages;
using Moodle.Presentation.Tools.Pedagogy;
using Moodle.Presentation.Tools.Portal;
using Moodle.Presentation.Tools.Risk;
using Moodle.Presentation.Tools.Reports;

namespace Moodle.Application.Tests.Tools;

public sealed class McpToolMetadataTests
{
    [Fact]
    public void ToolsMoodleDevemUsarHintsConservadoresParaAppsSdk()
    {
        foreach (var (toolType, method, attribute) in EnumerateToolAttributes())
        {
            var toolName = attribute.Name ?? method.Name;

            Assert.False(attribute.OpenWorld, $"{toolName} deve declarar OpenWorld=false.");
            if (toolName is "confirm_batch_grade_launch" or "confirm_forum_post" or
                "confirm_welcome_message" or "confirm_access_reminder" or "confirm_activity_reminder" or
                "confirm_recovery_message" or "confirm_closing_message" or "confirm_followup_message" or
                "manage_user_memory" or "save_user_memory_document" or "remove_user_memory_document" or
                "cancel_assisted_grading_batch" or "cancel_grading_batch" or
                "moodle_confirm_write")
            {
                Assert.True(attribute.Destructive, $"{toolName} deve declarar Destructive=true.");
            }
            else
            {
                Assert.False(attribute.Destructive, $"{toolName} deve declarar Destructive=false.");
            }

            if (toolName is "manage_user_memory" or "save_user_memory_document")
            {
                Assert.False(attribute.ReadOnly, $"{toolName} grava estado interno e deve declarar ReadOnly=false.");
                Assert.True(attribute.Idempotent, $"{toolName} deve ser idempotente.");
            }
            else if (toolName == "remove_user_memory_document")
            {
                Assert.False(attribute.ReadOnly, $"{toolName} remove estado interno e deve declarar ReadOnly=false.");
                Assert.True(attribute.Idempotent, $"{toolName} deve ser retry-safe pelo documentId.");
            }
            else if (toolName is "start_pending_grading_run" or "requeue_blocked_grading_items" or
                     "cancel_assisted_grading_batch" or "requeue_failed_grading_publication_items" or "cancel_grading_batch")
            {
                Assert.False(attribute.ReadOnly, $"{toolName} altera estado interno e deve declarar ReadOnly=false.");
                if (toolName == "start_pending_grading_run")
                {
                    Assert.False(attribute.Idempotent, $"{toolName} nao deve ser idempotente sem chave de idempotencia.");
                }
                else
                {
                    Assert.True(attribute.Idempotent, $"{toolName} deve ser idempotente por item e lote.");
                }
            }
            else if (toolName is "create_batch_grade_launch_preview" or "create_forum_post_preview")
            {
                Assert.False(attribute.ReadOnly, $"{toolName} cria acao pendente e deve declarar ReadOnly=false.");
                Assert.False(attribute.Idempotent, $"{toolName} nao deve ser idempotente sem chave de idempotencia.");
            }
            else if (toolName is "confirm_batch_grade_launch" or "confirm_forum_post")
            {
                Assert.False(attribute.ReadOnly, $"{toolName} executa escrita Moodle e deve declarar ReadOnly=false.");
                Assert.True(attribute.Idempotent, $"{toolName} deve ser retry-safe pelo pendingActionId.");
            }
            else if (toolName is "save_ai_grading_batch" or "refresh_grading_scale")
            {
                Assert.False(attribute.ReadOnly, $"{toolName} altera estado interno e deve declarar ReadOnly=false.");
                Assert.True(attribute.Idempotent, $"{toolName} deve ser idempotente com payload identico.");
            }
            else if (toolName is "prepare_welcome_message" or "prepare_access_reminder" or "prepare_activity_reminder" or
                     "prepare_recovery_message" or "prepare_closing_message" or "prepare_followup_message")
            {
                Assert.False(attribute.ReadOnly, $"{toolName} cria uma acao pendente e deve declarar ReadOnly=false.");
                Assert.False(attribute.Idempotent, $"{toolName} cria uma nova acao pendente a cada chamada.");
            }
            else if (toolName is "confirm_welcome_message" or "confirm_access_reminder" or "confirm_activity_reminder" or
                     "confirm_recovery_message" or "confirm_closing_message" or "confirm_followup_message" or
                     "moodle_prepare_write" or "moodle_confirm_write" or "moodle_reconcile_write")
            {
                Assert.False(attribute.ReadOnly, $"{toolName} altera estado e deve declarar ReadOnly=false.");
                if (toolName != "moodle_reconcile_write")
                {
                    Assert.False(attribute.Idempotent, $"{toolName} nao declara semantica retry-safe.");
                }
            }
            else
            {
                Assert.True(attribute.Idempotent, $"{toolName} deve declarar Idempotent=true.");
                Assert.True(attribute.ReadOnly, $"{toolName} deve declarar ReadOnly=true enquanto for tool de leitura Moodle.");
            }
        }
    }

    [Fact]
    public void AtualizacoesQuePodemSobrescreverEstadoDevemSerMarcadasComoDestrutivas()
    {
        Assert.True(GetToolAttribute(typeof(PortalTaskTools), "update_task").Destructive);
        Assert.True(GetToolAttribute(typeof(PortalAgendaTools), "update_agenda_event").Destructive);
    }

    private static IEnumerable<(Type ToolType, MethodInfo Method, McpServerToolAttribute Attribute)> EnumerateToolAttributes()
    {
        var toolTypes = new[]
        {
            typeof(MoodleCoursesTools),
            typeof(MoodleParticipantsTools),
            typeof(MoodleCourseContentsTools),
            typeof(MoodleCourseActivitiesTools),
            typeof(MoodleForumTools),
            typeof(MoodleAssignmentSubmissionsTools),
            typeof(MoodleGradingTools),
            typeof(MoodleGradebookTools),
            typeof(MoodleRiskAnalysisTools),
            typeof(MoodleReportTools),
            typeof(MoodleMemoryTools),
            typeof(MoodleMemoryDocumentTools),
            typeof(MoodlePedagogyTools),
            typeof(MoodleTutorMessageTools),
            typeof(MoodleUniversalWriteTools),
            typeof(MoodleBuildInfoTools)
        };

        foreach (var toolType in toolTypes)
        {
            foreach (var method in toolType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attribute is not null)
                {
                    yield return (toolType, method, attribute);
                }
            }
        }
    }

    private static McpServerToolAttribute GetToolAttribute(Type toolType, string toolName) =>
        toolType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Single(attribute => attribute?.Name == toolName)!;
}
