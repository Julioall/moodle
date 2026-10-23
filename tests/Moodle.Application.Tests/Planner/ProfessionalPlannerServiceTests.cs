using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;
using Moodle.Presentation;

namespace Moodle.Application.Tests.Planner;

public sealed class ProfessionalPlannerServiceTests
{
    private static (ConnectorDbContext Db, ProfessionalPlannerService Service, Guid User) CreateContext()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>().UseInMemoryDatabase($"professional-planner-{Guid.NewGuid():N}").Options;
        var db = new ConnectorDbContext(options); var user = Guid.NewGuid(); return (db, new ProfessionalPlannerService(db), user);
    }

    [Fact]
    public async Task Event_recorrente_expande_janela_com_exdate_rdate_e_override()
    {
        var (db, service, user) = CreateContext(); await using (db)
        {
            var start = new DateTimeOffset(2026, 9, 1, 13, 0, 0, TimeSpan.Zero);
            var created = await service.CreateEventAsync(user, user, new EventProfessionalInput("Aula semanal", StartAt: start, EndAt: start.AddHours(1), TimeZoneId: "America/Sao_Paulo", Recurrence: new("FREQ=WEEKLY;COUNT=4", [start.AddDays(7)], [start.AddDays(35)])), CancellationToken.None);
            var occurrences = await service.OccurrencesAsync(user, start.AddDays(-1), start.AddDays(50), null, null, CancellationToken.None);
            Assert.Equal(4, occurrences.Count);
            Assert.DoesNotContain(occurrences, x => x.OccurrenceStartAt == start.AddDays(7));
            await service.OverrideOccurrenceAsync(user, created.Id, start.AddDays(14), new(IsCancelled: true), CancellationToken.None);
            occurrences = await service.OccurrencesAsync(user, start.AddDays(-1), start.AddDays(50), null, null, CancellationToken.None);
            Assert.Equal(3, occurrences.Count);
            Assert.Contains(occurrences, x => x.OccurrenceStartAt == start.AddDays(35));
        }
    }

    [Fact]
    public async Task Event_que_comeca_antes_da_janela_mas_sobrepoe_o_intervalo_e_retorna()
    {
        var (db, service, user) = CreateContext(); await using (db)
        {
            var start = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
            await service.CreateEventAsync(user, user, new EventProfessionalInput("Plantão longo", StartAt: start, EndAt: start.AddHours(30), Recurrence: new("FREQ=WEEKLY;COUNT=3")), CancellationToken.None);
            var occurrences = await service.OccurrencesAsync(user, start.AddHours(24), start.AddHours(25), null, null, CancellationToken.None);
            Assert.Single(occurrences);
            Assert.Equal(start, occurrences[0].OccurrenceStartAt);
        }
    }

    [Fact]
    public async Task Task_subtask_progress_activity_e_dependencias_rejeitam_ciclo()
    {
        var (db, service, user) = CreateContext(); await using (db)
        {
            var root = await service.CreateTaskAsync(user, user, new TaskProfessionalInput("Investigar participação", Tags: ["turma"]), CancellationToken.None);
            var child = await service.CreateTaskAsync(user, user, new TaskProfessionalInput("Consultar registros", ParentTaskId: root.Id), CancellationToken.None);
            var rootWithChild = await service.GetTaskAsync(user, root.Id, CancellationToken.None);
            Assert.Equal(0, rootWithChild!.SubtaskProgress?.Done); Assert.Equal(1, rootWithChild.SubtaskProgress?.Total);
            await service.CompleteAsync(user, user, child.Id, true, null, CancellationToken.None);
            var detail = await service.GetTaskAsync(user, root.Id, CancellationToken.None);
            Assert.Equal(100m, detail!.SubtaskProgress!.Percent);
            Assert.Contains(await db.TaskActivities.Where(x => x.TaskId == child.Id).Select(x => x.EventType).ToArrayAsync(), x => x == "task_completed");
            var other = await service.CreateTaskAsync(user, user, new TaskProfessionalInput("Ação seguinte"), CancellationToken.None);
            await service.AddDependencyAsync(user, user, root.Id, other.Id, CancellationToken.None);
            await Assert.ThrowsAsync<ArgumentException>(() => service.AddDependencyAsync(user, user, other.Id, root.Id, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Criacao_inline_de_subtarefas_e_dependencias_persiste_progresso_e_owners()
    {
        var (db, service, user) = CreateContext(); await using (db)
        {
            var dependency = await service.CreateTaskAsync(user, user, new TaskProfessionalInput("Pré-requisito"), CancellationToken.None);
            var collaborator = Guid.NewGuid();
            var root = await service.CreateTaskAsync(user, user, new TaskProfessionalInput(
                "Plano operacional",
                Subtasks: [new TaskSubtaskInput("Revisar dados", OwnerId: collaborator), new TaskSubtaskInput("Validar com coordenação")],
                DependsOnTaskIds: [dependency.Id]), CancellationToken.None);
            var detail = await service.GetTaskAsync(user, root.Id, CancellationToken.None);
            Assert.Equal(2, detail!.Subtasks.Count);
            Assert.Equal(2, detail.SubtaskProgress!.Total);
            Assert.Contains(detail.DependsOn, id => id == dependency.Id);
            Assert.Contains(detail.Subtasks, item => item.Owner?.UserId == collaborator);
            Assert.Equal(2, await db.TaskActivities.CountAsync(item => item.TaskId == root.Id && item.EventType == "subtask_created"));
        }
    }

    [Fact]
    public void Ics_parser_preserva_timezone_recorrencia_e_excecoes()
    {
        var imported = PlannerIcsService.Parse("""
BEGIN:VCALENDAR
BEGIN:VEVENT
UID:series-1
DTSTART;TZID=America/Sao_Paulo:20260901T100000
DTEND;TZID=America/Sao_Paulo:20260901T110000
RRULE:FREQ=WEEKLY;COUNT=5
EXDATE;TZID=America/Sao_Paulo:20260908T100000
RDATE;TZID=America/Sao_Paulo:20261015T100000
LOCATION:Sala 3
SUMMARY:Orientação
END:VEVENT
END:VCALENDAR
""");
        var item = Assert.Single(imported); Assert.Equal("America/Sao_Paulo", item.TimeZoneId); Assert.Equal("FREQ=WEEKLY;COUNT=5", item.RRule); Assert.Single(item.ExDates!); Assert.Single(item.RDates!); Assert.Equal("Sala 3", item.Location);
    }

    [Fact]
    public async Task Reimportacao_ics_com_mesmo_uid_atualiza_o_mesmo_evento()
    {
        var (db, service, user) = CreateContext(); await using (db)
        {
            var first = Assert.Single(PlannerIcsService.Parse("""
BEGIN:VCALENDAR
BEGIN:VEVENT
UID:stable-event
DTSTART;TZID=America/Sao_Paulo:20260901T100000
DTEND;TZID=America/Sao_Paulo:20260901T110000
SUMMARY:Primeiro título
END:VEVENT
END:VCALENDAR
"""));
            var second = Assert.Single(PlannerIcsService.Parse("""
BEGIN:VCALENDAR
BEGIN:VEVENT
UID:stable-event
DTSTART;TZID=America/Sao_Paulo:20260901T100000
DTEND;TZID=America/Sao_Paulo:20260901T110000
SUMMARY:Título atualizado
END:VEVENT
END:VCALENDAR
"""));
            var created = await service.ImportEventAsync(user, user, first, CancellationToken.None);
            var updated = await service.ImportEventAsync(user, user, second, CancellationToken.None);
            Assert.True(created.Created);
            Assert.False(updated.Created);
            Assert.Equal(created.Event.Id, updated.Event.Id);
            Assert.Equal("Título atualizado", updated.Event.Title);
            Assert.Equal(1, await db.CalendarEvents.CountAsync(x => x.OwnerId == user));
        }
    }

    [Fact]
    public async Task Reagendar_evento_registra_activity_sem_alterar_status_da_task()
    {
        var (db, service, user) = CreateContext(); await using (db)
        {
            var task = await service.CreateTaskAsync(user, user, new TaskProfessionalInput("Acompanhar encontro"), CancellationToken.None);
            var start = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
            var calendarEvent = await service.CreateEventAsync(user, user, new EventProfessionalInput("Encontro", StartAt: start, EndAt: start.AddHours(1)), CancellationToken.None);
            await service.LinkAsync(user, user, task.Id, new(calendarEvent.Id), CancellationToken.None);
            await service.UpdateEventAsync(user, user, calendarEvent.Id, new EventProfessionalInput(StartAt: start.AddHours(2), ExpectedVersion: calendarEvent.Version), CancellationToken.None);
            var unchangedTask = await service.GetTaskAsync(user, task.Id, CancellationToken.None);
            Assert.Equal("todo", unchangedTask!.Status);
            Assert.Contains(await db.TaskActivities.Where(x => x.TaskId == task.Id).Select(x => x.EventType).ToArrayAsync(), x => x == "event_rescheduled");
        }
    }

    [Fact]
    public async Task Vinculo_de_ocorrencia_exige_uma_ocorrencia_real_e_nao_cancelada()
    {
        var (db, service, user) = CreateContext(); await using (db)
        {
            var start = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
            var calendarEvent = await service.CreateEventAsync(user, user, new EventProfessionalInput("Série", StartAt: start, EndAt: start.AddHours(1), Recurrence: new("FREQ=WEEKLY;COUNT=3")), CancellationToken.None);
            var task = await service.CreateTaskAsync(user, user, new TaskProfessionalInput("Tarefa"), CancellationToken.None);
            await service.OverrideOccurrenceAsync(user, calendarEvent.Id, start.AddDays(7), new(IsCancelled: true), CancellationToken.None);
            await Assert.ThrowsAsync<ArgumentException>(() => service.LinkAsync(user, user, task.Id, new(calendarEvent.Id, start.AddDays(7)), CancellationToken.None));
            await service.LinkAsync(user, user, task.Id, new(calendarEvent.Id, start.AddDays(14)), CancellationToken.None);
            await Assert.ThrowsAsync<ArgumentException>(() => service.LinkAsync(user, user, task.Id, new(calendarEvent.Id, start.AddDays(1)), CancellationToken.None));
        }
    }
}
