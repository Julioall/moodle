namespace Moodle.Application.Abstractions;

/// <summary>
/// Gateway para acesso ao Report Builder personalizado do Moodle via Web Services nativos.
/// Utiliza core_reportbuilder_list_reports e core_reportbuilder_retrieve_report.
/// </summary>
public interface IMoodleReportBuilderGateway
{
    /// <summary>
    /// Lista os relat&#xF3;rios personalizados acess&#xED;veis ao usu&#xE1;rio associado ao token.
    /// </summary>
    Task<IReadOnlyList<MoodleReportInfo>> ListReportsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Recupera as linhas de um relat&#xF3;rio personalizado, com pagina&#xE7;&#xE3;o autom&#xE1;tica.
    /// Se filtros forem fornecidos, aplica set_filters antes e reset_filters ap&#xF3;s a recupera&#xE7;&#xE3;o.
    /// </summary>
    Task<MoodleReportResult> DownloadAsync(
        int reportId,
        int pageSize,
        IDictionary<string, object?>? filters,
        CancellationToken cancellationToken);
}

public sealed record MoodleReportResult(
    DateTimeOffset UpdatedAt,
    IReadOnlyList<Dictionary<string, object?>> Rows,
    int TotalAvailable,
    bool IsTruncated);

public sealed record MoodleReportInfo(int Id, string Name, string Source);
