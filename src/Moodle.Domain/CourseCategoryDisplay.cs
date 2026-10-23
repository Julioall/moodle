namespace Moodle.Domain;

public static class CourseCategoryDisplay
{
    public const string PendingLabel = "Categoria não carregada";
    public const string UncategorizedLabel = "Sem categoria";

    public static string GetName(long? categoryId, string? categoryName)
    {
        if (categoryId == 0)
        {
            return UncategorizedLabel;
        }

        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            return categoryName;
        }

        return PendingLabel;
    }

    public static bool NeedsResolution(long? categoryId, string? categoryName) =>
        string.IsNullOrWhiteSpace(categoryName) && categoryId != 0;
}
