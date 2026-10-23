namespace Moodle.Infrastructure.Reports.Templates;

public sealed class ReportTemplateCatalogStateEntity
{
    public Guid OwnerId { get; set; }
    public DateTimeOffset DefaultsSeededAt { get; set; }
}
