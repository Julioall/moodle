using Moodle.Infrastructure.Configuration;
using InfrastructureDependencyInjection = Moodle.Infrastructure.DependencyInjection;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleTransportOptionsTests
{
    [Fact]
    public void ReadMaxConnectionsPerServerDefaultsToOne()
    {
        var options = new MoodleTransportOptions();

        Assert.Equal(1, options.ReadMaxConnectionsPerServer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void ReadMaxConnectionsPerServerRejectsValuesOutsideRange(int value)
    {
        var options = new MoodleTransportOptions
        {
            ReadMaxConnectionsPerServer = value
        };

        Assert.False(MoodleTransportOptions.IsValidReadMaxConnectionsPerServer(options.ReadMaxConnectionsPerServer));
    }

    [Fact]
    public void ReadHandlerUsesConfiguredLimitWhileWriteHandlerStaysAtOne()
    {
        using var readHandler = InfrastructureDependencyInjection.CreatePooledHandler(4);
        using var writeHandler = InfrastructureDependencyInjection.CreateFreshWriteHandler();

        Assert.Equal(4, readHandler.MaxConnectionsPerServer);
        Assert.Equal(1, writeHandler.MaxConnectionsPerServer);
    }
}
