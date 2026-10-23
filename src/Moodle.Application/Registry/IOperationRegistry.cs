using Moodle.Domain.Registry;

namespace Moodle.Application.Registry;

public interface IOperationRegistry
{
    MoodleOperation? GetOperation(string operationName);
    IReadOnlyList<MoodleOperation> GetAllOperations();
}
