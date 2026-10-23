using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moodle.Infrastructure;
using Moodle.Presentation.Security;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class PlatformRequestMetricsBufferTests
{
    [Fact]
    public void TryEnqueueNaoBloqueiaQuandoFilaLimitadaEstaCheia()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var buffer = new PlatformRequestMetricsBuffer(provider.GetRequiredService<IServiceScopeFactory>(), capacity: 1);
        var metric = new PlatformRequestMetricEntity { Method = "GET", Endpoint = "/api/courses", StatusCode = 200 };

        Assert.True(buffer.TryEnqueue(metric));
        Assert.False(buffer.TryEnqueue(metric));
    }

    [Fact]
    public async Task StopAsync_DrenaMetricasAceitasAntesDeEncerrar()
    {
        var databaseName = $"metrics-buffer-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<ConnectorDbContext>(options => options.UseInMemoryDatabase(databaseName));
        await using var provider = services.BuildServiceProvider();
        var buffer = new PlatformRequestMetricsBuffer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            capacity: 4);

        await buffer.StartAsync(CancellationToken.None);

        Assert.True(buffer.TryEnqueue(new PlatformRequestMetricEntity
        {
            Method = "GET",
            Endpoint = "/api/courses",
            StatusCode = 200,
        }));

        await WaitUntilAsync(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            return await db.PlatformRequestMetrics.CountAsync() == 1;
        });

        Assert.True(buffer.TryEnqueue(new PlatformRequestMetricEntity
        {
            Method = "GET",
            Endpoint = "/api/courses/{id}",
            StatusCode = 200,
        }));

        await buffer.StopAsync(CancellationToken.None);

        await using var verificationScope = provider.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        Assert.Equal(2, await verificationDb.PlatformRequestMetrics.CountAsync());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("A condição não foi satisfeita dentro do prazo de teste.");
    }
}
