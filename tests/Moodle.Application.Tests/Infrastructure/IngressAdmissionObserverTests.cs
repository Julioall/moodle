using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Options;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Security;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class IngressAdmissionObserverTests
{
    private static readonly AdmissionRouteMetadata CourseCatalog = new(AdmissionTrafficClass.LocalRead, "course_catalog");

    [Fact]
    public void Applies_burst_and_refills_at_configured_sustained_rate()
    {
        var clock = new ManualTimeProvider();
        using var observer = CreateObserver(clock);
        var context = CreateContext(Guid.NewGuid());

        for (var request = 0; request < 40; request++)
        {
            Assert.True(observer.Evaluate(context, CourseCatalog).IsAllowed);
        }

        var rejected = observer.Evaluate(context, CourseCatalog);
        Assert.False(rejected.IsAllowed);
        Assert.Equal(TimeSpan.FromMilliseconds(500), rejected.RetryAfter);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(observer.Evaluate(context, CourseCatalog).IsAllowed);
    }

    [Fact]
    public void Synthetic_25_page_catalog_plus_control_reads_fits_the_candidate_burst()
    {
        using var observer = CreateObserver(new ManualTimeProvider());
        var context = CreateContext(Guid.NewGuid());

        // 25 catalog pages and five local control reads in one burst.
        var decisions = Enumerable.Range(0, 30)
            .Select(_ => observer.Evaluate(context, CourseCatalog))
            .ToArray();

        Assert.All(decisions, decision => Assert.True(decision.IsAllowed));
        Assert.Equal(1, observer.TrackedPartitionCount);
    }

    [Fact]
    public void Partitions_authenticated_owners_and_explicit_families_independently()
    {
        using var observer = CreateObserver(new ManualTimeProvider());
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        for (var request = 0; request < 40; request++)
        {
            Assert.True(observer.Evaluate(CreateContext(ownerA), CourseCatalog).IsAllowed);
        }

        Assert.False(observer.Evaluate(CreateContext(ownerA), CourseCatalog).IsAllowed);
        Assert.True(observer.Evaluate(CreateContext(ownerB), CourseCatalog).IsAllowed);
        Assert.True(observer.Evaluate(
            CreateContext(ownerA),
            new AdmissionRouteMetadata(AdmissionTrafficClass.LocalRead, "course_preferences")).IsAllowed);
    }

    [Fact]
    public void Twenty_owners_behind_the_same_ip_receive_independent_quotas()
    {
        using var observer = CreateObserver(new ManualTimeProvider());
        var owners = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var sharedAddress = IPAddress.Parse("198.51.100.25");

        foreach (var owner in owners)
        {
            var context = CreateContext(owner, sharedAddress);
            for (var request = 0; request < 40; request++)
            {
                Assert.True(observer.Evaluate(context, CourseCatalog).IsAllowed);
            }

            Assert.False(observer.Evaluate(context, CourseCatalog).IsAllowed);
        }

        Assert.Equal(20, observer.TrackedPartitionCount);
    }

    [Fact]
    public void Authenticated_local_read_path_ids_and_query_variants_share_one_quota()
    {
        using var observer = CreateObserver(new ManualTimeProvider(), burst: 1);
        var ownerId = Guid.NewGuid();
        var endpoint = CreateRouteEndpoint("GET", "/api/tasks/{id:guid}");
        var metadata = AdmissionEndpointClassifier.Classify(endpoint);

        Assert.Equal(AdmissionTrafficClass.LocalRead, metadata?.TrafficClass);
        Assert.Equal("tasks", metadata?.Family);

        var requests = new[]
        {
            (Path: $"/api/tasks/{Guid.NewGuid():D}", Query: "?page=1&pageSize=20"),
            (Path: $"/api/tasks/{Guid.NewGuid():D}", Query: "?page=2&pageSize=50&search=active"),
            (Path: $"/api/tasks/{Guid.NewGuid():D}", Query: "?status=open")
        };

        var decisions = requests.Select(request =>
        {
            var context = CreateContext(ownerId);
            context.Request.Method = "GET";
            context.Request.Path = request.Path;
            context.Request.QueryString = new QueryString(request.Query);
            context.SetEndpoint(endpoint);
            return observer.Evaluate(context, metadata!);
        }).ToArray();

        Assert.True(decisions[0].IsAllowed);
        Assert.All(decisions.Skip(1), decision => Assert.False(decision.IsAllowed));
        Assert.Equal(1, observer.TrackedPartitionCount);
    }

    [Fact]
    public void Moodle_alias_and_course_id_variants_keep_the_registered_route_family()
    {
        var endpoint = CreateRouteEndpoint("GET", "/api/courses/{connectionRef}/{courseId}");
        var metadata = AdmissionEndpointClassifier.Classify(endpoint);

        Assert.Equal(AdmissionTrafficClass.RemoteRead, metadata?.TrafficClass);
        Assert.Equal("course_details", metadata?.Family);

        var requestVariants = new[]
        {
            (Path: "/api/courses/goias/101", Query: "?include=activities"),
            (Path: "/api/courses/senai/202", Query: "?include=forums&page=2")
        };

        foreach (var request in requestVariants)
        {
            var context = CreateContext(Guid.NewGuid());
            context.Request.Path = request.Path;
            context.Request.QueryString = new QueryString(request.Query);
            context.SetEndpoint(endpoint);

            Assert.Equal(metadata, AdmissionEndpointClassifier.Classify(context.GetEndpoint()!));
        }
    }

    [Fact]
    public void Read_and_mutation_methods_on_one_authenticated_route_keep_the_same_family()
    {
        var read = AdmissionEndpointClassifier.Classify(
            CreateRouteEndpoint("GET", "/api/course-preferences/ignored"));
        var mutation = AdmissionEndpointClassifier.Classify(
            CreateRouteEndpoint("PUT", "/api/course-preferences/ignored"));

        Assert.Equal("course_preferences", read?.Family);
        Assert.Equal("course_preferences", mutation?.Family);
        Assert.Equal(AdmissionTrafficClass.LocalRead, read?.TrafficClass);
        Assert.Equal(AdmissionTrafficClass.LocalMutation, mutation?.TrafficClass);
    }

    [Fact]
    public void Ignores_untrusted_forwarded_for_and_uses_only_processed_remote_address()
    {
        using var observer = CreateObserver(new ManualTimeProvider());
        var remoteAddress = IPAddress.Parse("198.51.100.25");

        for (var request = 0; request < 40; request++)
        {
            var context = CreateContext(null, remoteAddress);
            context.Request.Headers["X-Forwarded-For"] = $"203.0.113.{request + 1}";
            Assert.True(observer.Evaluate(context, CourseCatalog).IsAllowed);
        }

        var next = CreateContext(null, remoteAddress);
        next.Request.Headers["X-Forwarded-For"] = "192.0.2.10";
        Assert.False(observer.Evaluate(next, CourseCatalog).IsAllowed);
    }

    [Fact]
    public void Does_not_trust_owner_claims_from_an_unauthenticated_identity()
    {
        using var observer = CreateObserver(new ManualTimeProvider());
        var remoteAddress = IPAddress.Parse("198.51.100.25");

        for (var request = 0; request < 40; request++)
        {
            var context = CreateContext(null, remoteAddress);
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", Guid.NewGuid().ToString("D"))]));
            Assert.True(observer.Evaluate(context, CourseCatalog).IsAllowed);
        }

        var next = CreateContext(null, remoteAddress);
        next.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", Guid.NewGuid().ToString("D"))]));
        Assert.False(observer.Evaluate(next, CourseCatalog).IsAllowed);
        Assert.Equal(1, observer.TrackedPartitionCount);
    }

    [Fact]
    public void Bounds_partition_state_and_expires_only_idle_partitions()
    {
        var clock = new ManualTimeProvider();
        using var observer = CreateObserver(clock, maximumPartitions: 1, idleExpirationMinutes: 1);
        var firstOwner = CreateContext(Guid.NewGuid());
        var secondOwner = CreateContext(Guid.NewGuid());

        Assert.False(observer.Evaluate(firstOwner, CourseCatalog).UsesOverflowPartition);
        var overflow = observer.Evaluate(secondOwner, CourseCatalog);
        Assert.True(overflow.UsesOverflowPartition);
        Assert.Equal(1, observer.TrackedPartitionCount);

        clock.Advance(TimeSpan.FromMinutes(2));
        for (var request = 0; request < 253; request++)
        {
            _ = observer.Evaluate(secondOwner, CourseCatalog);
        }

        var afterIdleCleanup = observer.Evaluate(secondOwner, CourseCatalog);
        Assert.False(afterIdleCleanup.UsesOverflowPartition);
        Assert.Equal(1, observer.TrackedPartitionCount);
    }

    [Fact]
    public async Task Observe_mode_records_candidate_decisions_without_blocking_the_endpoint()
    {
        using var observer = CreateObserver(new ManualTimeProvider());
        var endpointBuilder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/courses"), 0);
        endpointBuilder.Metadata.Add(CourseCatalog);
        var context = CreateContext(Guid.NewGuid());
        context.SetEndpoint(endpointBuilder.Build());
        var endpointExecutions = 0;
        var middleware = new IngressAdmissionObservationMiddleware(
            _ =>
            {
                endpointExecutions++;
                return Task.CompletedTask;
            },
            Options.Create(new AdmissionRolloutOptions { IngressMode = AdmissionMode.Observe, LocalReadBurst = 1 }),
            observer);

        await middleware.InvokeAsync(context);
        await middleware.InvokeAsync(context);

        Assert.Equal(2, endpointExecutions);
        Assert.Equal(1, observer.TrackedPartitionCount);
    }

    private static IngressAdmissionObserver CreateObserver(
        TimeProvider timeProvider,
        int maximumPartitions = 10_000,
        int idleExpirationMinutes = 30,
        int burst = 40) => new(
            Options.Create(new AdmissionRolloutOptions
            {
                LocalReadBurst = burst,
                MaximumTrackedPartitions = maximumPartitions,
                IdlePartitionExpirationMinutes = idleExpirationMinutes
            }),
            timeProvider);

    private static Endpoint CreateRouteEndpoint(string method, string routePattern)
    {
        var endpointBuilder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(routePattern),
            0);
        endpointBuilder.Metadata.Add(new HttpMethodMetadata([method]));
        return endpointBuilder.Build();
    }

    private static DefaultHttpContext CreateContext(Guid? ownerId, IPAddress? remoteAddress = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteAddress ?? IPAddress.Parse("198.51.100.25");
        if (ownerId is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, ownerId.Value.ToString("D"))],
                authenticationType: "test"));
        }

        return context;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
