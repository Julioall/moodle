namespace Moodle.Application.Reports.Templates;

public sealed class ReportReadBudget(int maxGradeReads)
{
    public int GradeReads { get; private set; }
    public bool TryGradeRead()
    {
        if (GradeReads >= maxGradeReads) return false;
        GradeReads++;
        return true;
    }
}

public sealed class ReportDatasetException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
