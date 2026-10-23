namespace Moodle.Application.Tests.Domain;

public class PagedCoursesTests
{
    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(100, 10, 10)]
    [InlineData(101, 10, 11)]
    [InlineData(1, 1, 1)]
    [InlineData(5, 1, 5)]
    public void TotalPages_deve_arredondar_para_cima(int totalCount, int pageSize, int expected)
    {
        var paged = new Moodle.Domain.PagedCourses([], totalCount, 1, pageSize);
        Assert.Equal(expected, paged.TotalPages);
    }

    [Fact]
    public void TotalPages_deve_retornar_zero_quando_pageSize_for_zero()
    {
        var paged = new Moodle.Domain.PagedCourses([], 10, 1, 0);
        Assert.Equal(0, paged.TotalPages);
    }

    [Theory]
    [InlineData(1, 10, 30, true)]   // pagina 1 de 3 => tem proxima
    [InlineData(2, 10, 30, true)]   // pagina 2 de 3 => tem proxima
    [InlineData(3, 10, 30, false)]  // pagina 3 de 3 => nao tem proxima
    [InlineData(1, 10, 10, false)]  // pagina 1 de 1 => nao tem proxima
    public void HasNextPage_deve_refletir_existencia_de_proxima_pagina(int page, int pageSize, int totalCount, bool expected)
    {
        var paged = new Moodle.Domain.PagedCourses([], totalCount, page, pageSize);
        Assert.Equal(expected, paged.HasNextPage);
    }

    [Theory]
    [InlineData(1, false)]  // primeira pagina => nao tem anterior
    [InlineData(2, true)]   // segunda pagina => tem anterior
    [InlineData(5, true)]   // qualquer pagina > 1 => tem anterior
    public void HasPreviousPage_deve_refletir_existencia_de_pagina_anterior(int page, bool expected)
    {
        var paged = new Moodle.Domain.PagedCourses([], 50, page, 10);
        Assert.Equal(expected, paged.HasPreviousPage);
    }
}
