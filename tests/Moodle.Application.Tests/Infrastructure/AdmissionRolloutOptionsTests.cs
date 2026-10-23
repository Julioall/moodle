using Moodle.Presentation.Configuration;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class AdmissionRolloutOptionsTests
{
    [Fact]
    public void Defaults_to_legacy_independently_for_ingress_and_moodle_egress()
    {
        var options = new AdmissionRolloutOptions();

        Assert.Equal(AdmissionMode.Legacy, options.IngressMode);
        Assert.Equal(AdmissionMode.Legacy, options.MoodleEgressMode);
        Assert.True(options.IsSupportedInCurrentStage);
        Assert.True(options.HasValidIngressProfile);
        Assert.Equal(120, options.LocalReadPermitLimitPerMinute);
        Assert.Equal(40, options.LocalReadBurst);
    }

    [Theory]
    [InlineData(AdmissionMode.Legacy, AdmissionMode.Observe)]
    [InlineData(AdmissionMode.Observe, AdmissionMode.Legacy)]
    [InlineData(AdmissionMode.Observe, AdmissionMode.Observe)]
    public void Allows_legacy_and_observe_modes_independently(
        AdmissionMode ingressMode,
        AdmissionMode moodleEgressMode)
    {
        var options = new AdmissionRolloutOptions
        {
            IngressMode = ingressMode,
            MoodleEgressMode = moodleEgressMode
        };

        Assert.True(options.IsSupportedInCurrentStage);
    }

    [Theory]
    [InlineData(AdmissionMode.Enforce, AdmissionMode.Legacy)]
    [InlineData(AdmissionMode.Legacy, AdmissionMode.Enforce)]
    public void Rejects_enforce_until_policy_is_implemented(
        AdmissionMode ingressMode,
        AdmissionMode moodleEgressMode)
    {
        var options = new AdmissionRolloutOptions
        {
            IngressMode = ingressMode,
            MoodleEgressMode = moodleEgressMode
        };

        Assert.False(options.IsSupportedInCurrentStage);
    }

    [Theory]
    [InlineData(0, 40, 10_000, 30)]
    [InlineData(120, 0, 10_000, 30)]
    [InlineData(120, 40, 0, 30)]
    [InlineData(120, 40, 10_000, 0)]
    [InlineData(10_001, 40, 10_000, 30)]
    [InlineData(120, 1_001, 10_000, 30)]
    [InlineData(120, 40, 100_001, 30)]
    [InlineData(120, 40, 10_000, 1_441)]
    public void Invalid_candidate_profile_values_fail_validation(
        int permitLimit,
        int burst,
        int maximumPartitions,
        int idleExpirationMinutes)
    {
        var options = new AdmissionRolloutOptions
        {
            LocalReadPermitLimitPerMinute = permitLimit,
            LocalReadBurst = burst,
            MaximumTrackedPartitions = maximumPartitions,
            IdlePartitionExpirationMinutes = idleExpirationMinutes
        };

        Assert.False(options.HasValidIngressProfile);
    }
}
