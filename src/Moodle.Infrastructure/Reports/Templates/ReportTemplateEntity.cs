namespace Moodle.Infrastructure.Reports.Templates;

public sealed class ReportTemplateEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string SourceId { get; set; } = "student_course";
    public string DefinitionJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
