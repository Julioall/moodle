namespace Moodle.Domain;

public enum CourseReadSource
{
    Unknown,
    Moodle,
    MemoryCache,
    Stub,
}

/// <summary>
/// Resultado paginado de uma listagem de cursos.
/// </summary>
/// <param name="Items">Cursos da página atual.</param>
/// <param name="TotalCount">Total de cursos disponíveis para o usuário (todas as páginas).</param>
/// <param name="Page">Número da página atual (base 1).</param>
/// <param name="PageSize">Quantidade de itens por página.</param>
public sealed record PagedCourses(
    IReadOnlyList<CourseSummary> Items,
    int TotalCount,
    int Page,
    int PageSize,
    CourseReadSource Source = CourseReadSource.Unknown)
{
    /// <summary>Total de páginas disponíveis.</summary>
    public int TotalPages => PageSize > 0 ? (TotalCount + PageSize - 1) / PageSize : 0;

    /// <summary>Indica se existe uma próxima página.</summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>Indica se existe uma página anterior.</summary>
    public bool HasPreviousPage => Page > 1;
}
