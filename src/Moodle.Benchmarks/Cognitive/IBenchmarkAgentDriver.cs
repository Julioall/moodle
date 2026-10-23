using System.Threading;
using System.Threading.Tasks;

namespace Moodle.Benchmarks.Cognitive;

public interface IBenchmarkAgentDriver
{
    Task<CognitiveTrace> RunAsync(
        BenchmarkTask task,
        BenchmarkProfile profile,
        CancellationToken cancellationToken);
}
