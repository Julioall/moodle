using Moodle.Domain;

namespace Moodle.Application.Tests.Courses;

public sealed class CourseCategoryDisplayTests
{
    [Fact]
    public void GetName_PreservesResolvedName()
    {
        Assert.Equal("Escola > Turma", CourseCategoryDisplay.GetName(42, "Escola > Turma"));
        Assert.False(CourseCategoryDisplay.NeedsResolution(42, "Escola > Turma"));
    }

    [Fact]
    public void GetName_OnlyLabelsExplicitZeroAsUncategorized()
    {
        Assert.Equal("Sem categoria", CourseCategoryDisplay.GetName(0, null));
        Assert.Equal("Sem categoria", CourseCategoryDisplay.GetName(0, "Unexpected label"));
        Assert.False(CourseCategoryDisplay.NeedsResolution(0, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(42L)]
    public void GetName_LabelsMissingCategoryMetadataAsPending(long? categoryId)
    {
        Assert.Equal("Categoria não carregada", CourseCategoryDisplay.GetName(categoryId, null));
        Assert.True(CourseCategoryDisplay.NeedsResolution(categoryId, null));
    }
}
