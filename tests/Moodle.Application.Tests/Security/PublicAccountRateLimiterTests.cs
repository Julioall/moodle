using Microsoft.Extensions.Options;
using Moodle.Application.Tests.Infrastructure;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Security;

namespace Moodle.Application.Tests.Security;

public sealed class PublicAccountRateLimiterTests
{
    [Fact]
    public void Normalized_email_shares_login_quota_but_registration_has_its_own_quota()
    {
        using var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-08T12:00:00Z"));
        using var limiter = CreateLimiter(clock, permitLimit: 2);

        Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, " Teacher@Example.invalid ", out _));
        Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, "teacher@example.invalid", out _));
        Assert.False(limiter.TryAcquire(PublicAccountOperation.Login, "TEACHER@example.invalid", out var retryAfter));
        Assert.Equal(60, retryAfter);

        Assert.True(limiter.TryAcquire(PublicAccountOperation.Registration, "teacher@example.invalid", out _));
        Assert.True(limiter.TryAcquire(PublicAccountOperation.Registration, "TEACHER@example.invalid", out _));
        Assert.False(limiter.TryAcquire(PublicAccountOperation.Registration, "teacher@example.invalid", out _));
    }

    [Fact]
    public void Quota_resets_after_its_window()
    {
        using var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-08T12:00:00Z"));
        using var limiter = CreateLimiter(clock, permitLimit: 1);

        Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, "teacher@example.invalid", out _));
        Assert.False(limiter.TryAcquire(PublicAccountOperation.Login, "teacher@example.invalid", out var retryAfter));
        Assert.Equal(60, retryAfter);

        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, "teacher@example.invalid", out _));
    }

    [Fact]
    public void Initial_authentication_profile_allows_twelve_attempts_per_sixty_seconds()
    {
        using var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-08T12:00:00Z"));
        using var limiter = CreateLimiter(clock, permitLimit: 12);

        for (var attempt = 0; attempt < 12; attempt++)
        {
            Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, "teacher@example.invalid", out _));
        }

        Assert.False(limiter.TryAcquire(PublicAccountOperation.Login, "teacher@example.invalid", out var retryAfter));
        Assert.Equal(60, retryAfter);
    }

    [Fact]
    public void Partition_limit_uses_bounded_overflow_buckets_per_operation()
    {
        using var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-08T12:00:00Z"));
        using var limiter = CreateLimiter(clock, permitLimit: 2, maximumTrackedPartitions: 1);

        Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, "first@example.invalid", out _));
        Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, "second@example.invalid", out _));
        Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, "second@example.invalid", out _));
        Assert.False(limiter.TryAcquire(PublicAccountOperation.Login, "second@example.invalid", out _));

        Assert.True(limiter.TryAcquire(PublicAccountOperation.Registration, "second@example.invalid", out _));
        Assert.True(limiter.TryAcquire(PublicAccountOperation.Registration, "second@example.invalid", out _));
        Assert.False(limiter.TryAcquire(PublicAccountOperation.Registration, "second@example.invalid", out _));
    }

    [Fact]
    public void Idle_partitions_are_evicted_before_new_addresses_use_overflow()
    {
        using var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-08T12:00:00Z"));
        using var limiter = CreateLimiter(clock, permitLimit: 1, maximumTrackedPartitions: 1, idleExpirationMinutes: 1);

        Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, "first@example.invalid", out _));
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, "second@example.invalid", out _));
    }

    [Fact]
    public void Invalid_addresses_do_not_create_account_partitions()
    {
        using var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-08T12:00:00Z"));
        using var limiter = CreateLimiter(clock, permitLimit: 1, maximumTrackedPartitions: 1);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, "not an address", out _));
        }

        Assert.True(limiter.TryAcquire(PublicAccountOperation.Login, "teacher@example.invalid", out _));
        Assert.False(limiter.TryAcquire(PublicAccountOperation.Login, "teacher@example.invalid", out _));
    }

    private static PublicAccountRateLimiter CreateLimiter(
        TimeProvider clock,
        int permitLimit,
        int maximumTrackedPartitions = 10,
        int idleExpirationMinutes = 30) =>
        new(Options.Create(new ConnectorRateLimitOptions
        {
            WindowSeconds = 60,
            AccountAuthPermitLimit = permitLimit,
            AccountAuthMaximumTrackedPartitions = maximumTrackedPartitions,
            AccountAuthIdleExpirationMinutes = idleExpirationMinutes
        }), clock);
}
