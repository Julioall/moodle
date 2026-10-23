using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Reports;

namespace Moodle.Application.Tests.Reports;

public sealed class ReportStorageCalculatorTests
{
    [Fact]
    public void GetBase64DecodedLengthCalculaOConteudoReal()
    {
        Assert.Equal(0, ReportStorageCalculator.GetBase64DecodedLength(string.Empty));
        Assert.Equal(3, ReportStorageCalculator.GetBase64DecodedLength(Convert.ToBase64String(new byte[3])));
        Assert.Equal(1024, ReportStorageCalculator.GetBase64DecodedLength(Convert.ToBase64String(new byte[1024])));
    }

    [Fact]
    public async Task GetUsedBytesSomaArquivosNovosELegadosApenasDoUsuario()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();

        dbContext.ReportJobs.AddRange(
            new ReportJobEntity { Id = Guid.NewGuid(), OwnerId = ownerId, Status = "completed", FileSizeBytes = 1024 },
            new ReportJobEntity { Id = Guid.NewGuid(), OwnerId = ownerId, Status = "completed", ContentBase64 = Convert.ToBase64String(new byte[3]) },
            new ReportJobEntity { Id = Guid.NewGuid(), OwnerId = ownerId, Status = "running", FileSizeBytes = 9999 },
            new ReportJobEntity { Id = Guid.NewGuid(), OwnerId = otherOwnerId, Status = "completed", FileSizeBytes = 5000 });
        await dbContext.SaveChangesAsync();

        var usedBytes = await ReportStorageCalculator.GetUsedBytesAsync(dbContext, ownerId);

        Assert.Equal(1027, usedBytes);
    }

    [Fact]
    public async Task GetUsedBytesFallsBackOnlyForZeroSizeLegacyRows()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new ConnectorDbContext(options);
        var ownerId = Guid.NewGuid();
        var excludedJobId = Guid.NewGuid();

        dbContext.ReportJobs.AddRange(
            new ReportJobEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                Status = "completed",
                FileSizeBytes = 0,
                ContentBase64 = Convert.ToBase64String(new byte[3]),
            },
            new ReportJobEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                Status = "completed",
                FileSizeBytes = 0,
                ContentText = "á",
            },
            new ReportJobEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                Status = "completed",
                FileSizeBytes = 8,
                ContentBase64 = Convert.ToBase64String(new byte[2048]),
            },
            new ReportJobEntity
            {
                Id = excludedJobId,
                OwnerId = ownerId,
                Status = "completed",
                FileSizeBytes = 0,
                ContentBase64 = Convert.ToBase64String(new byte[5]),
            },
            new ReportJobEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                Status = "failed",
                FileSizeBytes = 0,
                ContentBase64 = Convert.ToBase64String(new byte[100]),
            });
        await dbContext.SaveChangesAsync();

        var usedBytes = await ReportStorageCalculator.GetUsedBytesAsync(dbContext, ownerId, excludeJobId: excludedJobId);

        Assert.Equal(13, usedBytes);
    }
}
