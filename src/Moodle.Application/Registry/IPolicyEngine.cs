using Moodle.Domain.Registry;

namespace Moodle.Application.Registry;

public interface IPolicyEngine
{
    PolicyEvaluationResult Evaluate(MoodleOperation? operation);
}
