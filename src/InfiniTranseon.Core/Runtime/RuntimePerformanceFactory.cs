using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Scheduling;
using InfiniTranseon.Core.Settings;

namespace InfiniTranseon.Core.Runtime;

public static class RuntimePerformanceFactory
{
    public static RuntimePerformanceController Create(
        IRuntimeEngineHostSession session,
        ProfileDocument profile,
        long profileRevision,
        PerformanceRuntimeSettings settings,
        IPerformanceSnapshotSource source,
        Func<DegradationEvent, CancellationToken, ValueTask> report,
        TimeSpan commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfLessThan(profileRevision, 1);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(report);
        if (commandTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        settings.Validate();
        RegionPerformancePolicy[] regions = CreatePolicies(profile);
        if (regions.Length == 0)
            throw new ArgumentException("Performance governance requires at least one enabled profile region.", nameof(profile));
        var governor = new PerformanceGovernor(
            settings.Preset,
            new PerformanceGovernorOptions(
                settings.OverloadSamples,
                settings.RecoverySamples,
                settings.MinimumDwell,
                settings.CustomThresholds));
        return new RuntimePerformanceController(
            source,
            governor,
            regions,
            profile.ProfileId,
            profileRevision,
            (revision, cancellationToken) => session.ApplyPolicyAsync(
                revision, commandTimeout, cancellationToken),
            report,
            settings.SampleInterval);
    }

    internal static RegionPerformancePolicy[] CreatePolicies(ProfileDocument profile)
    {
        var policies = new List<RegionPerformancePolicy>();
        foreach (ProfileTarget target in profile.Targets.Where(target => target.Enabled))
        {
            foreach (ProfileRegion region in target.Regions.Where(region => region.Enabled))
                policies.Add(CreatePolicy(region));
            if (target.ScanRemainingArea && target.RemainingAreaRegion is { Enabled: true } remaining)
                policies.Add(CreatePolicy(remaining));
        }
        if (policies.Any(policy => policy.RegionId == Guid.Empty) ||
            policies.Select(policy => policy.RegionId).Distinct().Count() != policies.Count)
            throw new ArgumentException("Enabled performance regions must have unique identities.", nameof(profile));
        return policies.ToArray();
    }

    private static RegionPerformancePolicy CreatePolicy(ProfileRegion region) => new(
        region.RegionId,
        (int)region.Priority,
        region.LockDegradation,
        region.RecognitionInterval,
        region.AreaMode == CaptureAreaKind.RemainingArea,
        SmallerOcrModelConfigured: false,
        OptionalRefinementEnabled: region.TranslationChannels.Any(channel =>
            channel.Enabled && channel.RefinementSteps.Count > 0));
}
