namespace Moodle.Domain.Registry;

public enum OperationPolicy
{
    /// <summary>
    /// Operation can be called directly, mapping inputs directly to the Moodle Web Service.
    /// </summary>
    Direct = 0,

    /// <summary>
    /// Operation requires aggregation or a specialized domain service to map/compose before/after calling Moodle.
    /// </summary>
    Aggregated = 1
}
