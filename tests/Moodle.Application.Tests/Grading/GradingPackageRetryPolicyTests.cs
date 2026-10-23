using System.IO;
using Moodle.Application.Grading;
using Moodle.Application.Grading.Packages;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingPackageRetryPolicyTests
{
    [Fact]
    public void IsTransient_recognizes_wrapped_dependency_and_storage_failures()
    {
        Assert.True(GradingPackageRetryPolicy.IsTransient(
            new GradingTransientDependencyException("package_read_transient", "temporario")));
        Assert.True(GradingPackageRetryPolicy.IsTransient(
            new IOException("storage temporariamente indisponivel")));
        Assert.False(GradingPackageRetryPolicy.IsTransient(
            new InvalidOperationException("configuracao invalida")));
    }
}
