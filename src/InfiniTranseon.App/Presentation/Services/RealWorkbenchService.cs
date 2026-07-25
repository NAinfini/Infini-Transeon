using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Storage;

namespace InfiniTranseon.App.Presentation.Services;

public sealed class WorkbenchValidationException : Exception
{
    public WorkbenchValidationException(IReadOnlyList<ProfileValidationIssue> issues)
        : base(string.Join(
            Environment.NewLine,
            issues.Select(issue => $"{issue.Code}: {issue.Path}")))
    {
        Issues = issues;
    }

    public IReadOnlyList<ProfileValidationIssue> Issues { get; }
}

public sealed class RealWorkbenchService : IWorkbenchService
{
    private readonly ProfileRepository _profiles;
    private readonly IRuntimeControlService _runtime;
    private readonly RuntimeCapabilitiesService _capabilities;
    private readonly CustomRestAdapterStore? _customAdapters;

    public RealWorkbenchService(
        ProfileRepository profiles,
        IRuntimeControlService runtime,
        RuntimeCapabilitiesService capabilities,
        CustomRestAdapterStore? customAdapters = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(capabilities);
        _profiles = profiles;
        _runtime = runtime;
        _capabilities = capabilities;
        _customAdapters = customAdapters;
    }

    public async Task<WorkbenchProfileDraft?> LoadAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ProfileDocument? profile =
            await _profiles.LoadAsync(profileId, cancellationToken).ConfigureAwait(false);
        return profile is null ? null : ToDraft(profile);
    }

    public async Task<ProfileRuntimeApplyResult> SaveAndApplyAsync(
        WorkbenchProfileDraft profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ProfileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profile));
        ProfileDocument existing =
            await _profiles.LoadAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Profile was not found.");
        ProfileDocument candidate = Apply(existing, profile);
        var knownProviders = ProviderCatalog.Default
            .Where(provider =>
                provider.IsSelectable &&
                provider.Capability == CatalogProviderCapability.Translation)
            .Select(provider => provider.Id)
            .Concat(_customAdapters?.GetCatalogProviders()
                .Where(provider => provider.IsSelectable)
                .Select(provider => provider.Id) ?? [])
            .Concat(existing.Targets
                .SelectMany(target => target.Regions)
                .SelectMany(region => region.TranslationChannels)
                .SelectMany(channel => channel.FallbackProviderIds
                    .Append(channel.InitialProviderId)
                    .Concat(channel.RefinementSteps.Select(step => step.ProviderId))))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        ProfileValidationResult validation = new ProfileValidator().Validate(
            candidate,
            _capabilities.Capabilities,
            knownProviders);
        ProfileValidationIssue[] errors = validation.Issues
            .Where(issue => issue.Severity == ProfileIssueSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
            throw new WorkbenchValidationException(errors);

        await _profiles.SaveAsync(validation.Document, cancellationToken)
            .ConfigureAwait(false);
        return await _runtime.ApplyProfileAsync(profile.ProfileId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static WorkbenchProfileDraft ToDraft(ProfileDocument profile) => new(
        profile.ProfileId,
        profile.Name,
        profile.SourceLanguage,
        profile.TargetLanguage,
        profile.Context.GameName,
        profile.Context.GameDescription,
        profile.Context.RecentLineCount,
        profile.Targets.Select(target => new WorkbenchTargetDraft(
            target.TargetId,
            target.Name,
            target.Kind.ToString(),
            target.Enabled,
            target.DetectionLongEdge,
            target.ScanRemainingArea,
            checked((int)target.RemainingAreaInterval.TotalMilliseconds),
            target.Regions.Select(region => new WorkbenchRegionDraft(
                region.RegionId,
                region.Name,
                region.Enabled,
                region.Bounds.X,
                region.Bounds.Y,
                region.Bounds.Width,
                region.Bounds.Height,
                ProfilePresentationMapper.Priority(region.Priority),
                checked((int)region.RecognitionInterval.TotalMilliseconds),
                region.Ocr.ProviderId,
                region.Ocr.RecognitionLanguage,
                region.Ocr.DetectionScale,
                region.Ocr.DetectOrientation,
                region.Ocr.UseCloudOcr,
                region.Ocr.CloudConsentPolicyRevision,
                region.TranslationEnabled,
                region.TranslationChannels
                    .OrderBy(channel => channel.DisplayOrder)
                    .Select(channel => new WorkbenchChannelDraft(
                        channel.ChannelId,
                        channel.InitialProviderId,
                        channel.DisplayLabel,
                        channel.Enabled,
                        channel.DisplayOrder,
                        channel.RefinementSteps
                            .Select(step => step.ProviderId)
                            .ToArray()))
                    .ToArray(),
                region.LineBreakMode.ToString(),
                region.CustomLineSeparator,
                region.MaximumLines,
                region.LineAlignment.ToString(),
                ProfilePresentationMapper.ContextRole(region.ContextRole),
                region.Overlay.Mode.ToString(),
                region.Overlay.BackgroundMode.ToString(),
                region.Overlay.BackgroundColor,
                region.Overlay.TextColor,
                region.Overlay.Opacity,
                region.Overlay.BlurRadius,
                region.LockDegradation,
                region.Overlay.PreferredFontSize,
                region.Overlay.OutlineColor,
                region.Overlay.OutlineWidth))
                .ToArray()))
            .ToArray());

    private static ProfileDocument Apply(
        ProfileDocument existing,
        WorkbenchProfileDraft draft)
    {
        var existingTargets = existing.Targets.ToDictionary(target => target.TargetId);
        List<ProfileTarget> targets = draft.Targets.Select(target =>
        {
            existingTargets.TryGetValue(target.TargetId, out ProfileTarget? preservedTarget);
            var existingRegions = (preservedTarget?.Regions ?? [])
                .ToDictionary(region => region.RegionId);
            List<ProfileRegion> regions = target.Regions.Select(region =>
            {
                existingRegions.TryGetValue(region.RegionId, out ProfileRegion? preservedRegion);
                var existingChannels = (preservedRegion?.TranslationChannels ?? [])
                    .ToDictionary(channel => channel.ChannelId);
                List<ProfileTranslationChannel> channels = region.Channels
                    .Select((channel, index) =>
                    {
                        existingChannels.TryGetValue(
                            channel.ChannelId,
                            out ProfileTranslationChannel? preservedChannel);
                        return (preservedChannel ??
                            ProfileTranslationChannel.Create(channel.ProviderId)) with
                        {
                            ChannelId = channel.ChannelId == Guid.Empty
                                ? Guid.NewGuid()
                                : channel.ChannelId,
                            InitialProviderId = channel.ProviderId,
                            DisplayLabel = channel.Label,
                            Enabled = channel.Enabled,
                            DisplayOrder = index,
                            RefinementSteps = channel.EffectiveRefinementProviderIds
                                .Take(2)
                                .Select((providerId, refinementIndex) =>
                                {
                                    ProfileRefinementStep? preservedStep =
                                        preservedChannel?.RefinementSteps
                                            .ElementAtOrDefault(refinementIndex);
                                    return (preservedStep ?? new ProfileRefinementStep()) with
                                    {
                                        StageId = preservedStep?.StageId ?? Guid.NewGuid(),
                                        ProviderId = providerId,
                                        PromptTemplateId = "active-style-prompt",
                                    };
                                })
                                .ToList(),
                        };
                    })
                    .ToList();
                ProfileOcrSettings ocr = (preservedRegion?.Ocr ?? new()) with
                {
                    ProviderId = region.OcrProviderId,
                    RecognitionLanguage = region.RecognitionLanguage,
                    DetectionScale = region.DetectionScale,
                    DetectOrientation = region.DetectOrientation,
                    UseCloudOcr = region.UseCloudOcr,
                    CloudConsentPolicyRevision = region.UseCloudOcr
                        ? region.CloudConsentPolicyRevision
                        : 0,
                };
                ProfileOverlaySettings overlay = (preservedRegion?.Overlay ?? new()) with
                {
                    Mode = Parse(region.OverlayMode, OverlayMode.Replace),
                    BackgroundMode = Parse(
                        region.OverlayBackgroundMode,
                        OverlayBackgroundMode.AutomaticContrastBlur),
                    BackgroundColor = EmptyToNull(region.BackgroundColor),
                    TextColor = EmptyToNull(region.TextColor),
                    Opacity = region.OverlayOpacity,
                    BlurRadius = region.BlurRadius,
                    PreferredFontSize = region.PreferredFontSize,
                    OutlineColor = EmptyToNull(region.OutlineColor),
                    OutlineWidth = region.OutlineWidth,
                };
                return (preservedRegion ?? ProfileRegion.Create(
                    region.Name,
                    new NormalizedRect(region.X, region.Y, region.Width, region.Height))) with
                {
                    RegionId = region.RegionId == Guid.Empty
                        ? Guid.NewGuid()
                        : region.RegionId,
                    Name = region.Name,
                    Enabled = region.Enabled,
                    DisabledReasonCode = region.Enabled
                        ? null
                        : preservedRegion?.DisabledReasonCode,
                    Bounds = new NormalizedRect(
                        region.X,
                        region.Y,
                        region.Width,
                        region.Height),
                    Priority = ProfilePresentationMapper.Priority(region.Priority),
                    RecognitionInterval =
                        TimeSpan.FromMilliseconds(region.RecognitionIntervalMilliseconds),
                    Ocr = ocr,
                    TranslationEnabled = region.TranslationEnabled,
                    TranslationChannels = channels,
                    LineBreakMode = Parse(
                        region.LineBreakMode,
                        LineBreakMode.PreserveLines),
                    CustomLineSeparator = EmptyToNull(region.CustomLineSeparator),
                    MaximumLines = region.MaximumLines,
                    LineAlignment = Parse(region.LineAlignment, LineAlignment.Auto),
                    ContextRole = ProfilePresentationMapper.ContextRole(region.ContextRole),
                    Overlay = overlay,
                    LockDegradation = region.LockDegradation,
                };
            }).ToList();
            ProfileRegion? remainingArea = preservedTarget?.RemainingAreaRegion;
            if (target.ScanRemainingArea)
            {
                ProfileRegion? template = regions
                    .OrderBy(region => region.Priority)
                    .FirstOrDefault();
                List<ProfileTranslationChannel> channels = template?.TranslationChannels
                    .Select((channel, index) =>
                    {
                        ProfileTranslationChannel? preservedChannel =
                            remainingArea?.TranslationChannels.ElementAtOrDefault(index);
                        return channel with
                        {
                            ChannelId = preservedChannel?.ChannelId ?? Guid.NewGuid(),
                            RefinementSteps = channel.RefinementSteps
                                .Select((step, refinementIndex) => step with
                                {
                                    StageId = preservedChannel?.RefinementSteps
                                        .ElementAtOrDefault(refinementIndex)?.StageId ?? Guid.NewGuid(),
                                })
                                .ToList(),
                        };
                    })
                    .ToList() ?? [];
                remainingArea = (remainingArea ?? ProfileRegion.Create(
                    "Remaining area",
                    new NormalizedRect(0, 0, 1, 1))) with
                {
                    Name = string.IsNullOrWhiteSpace(remainingArea?.Name)
                        ? "Remaining area"
                        : remainingArea.Name,
                    Enabled = true,
                    DisabledReasonCode = null,
                    Bounds = new NormalizedRect(0, 0, 1, 1),
                    AreaMode = CaptureAreaKind.RemainingArea,
                    Priority = RegionPriority.P3,
                    RecognitionInterval =
                        TimeSpan.FromMilliseconds(target.RemainingAreaIntervalMilliseconds),
                    Ocr = template?.Ocr ?? remainingArea?.Ocr ?? new ProfileOcrSettings(),
                    TranslationEnabled = template?.TranslationEnabled ?? true,
                    TranslationChannels = channels,
                    LineBreakMode = template?.LineBreakMode ?? LineBreakMode.PerBox,
                    CustomLineSeparator = template?.CustomLineSeparator,
                    MaximumLines = template?.MaximumLines ?? 64,
                    LineAlignment = template?.LineAlignment ?? LineAlignment.Auto,
                    ContextRole = ProfileRegionContextRole.None,
                    Overlay = template?.Overlay ?? remainingArea?.Overlay ?? new ProfileOverlaySettings(),
                    LockDegradation = false,
                };
            }
            return (preservedTarget ?? ProfileTarget.Create(
                target.Name,
                Parse(target.Kind, CaptureTargetKind.Window))) with
            {
                TargetId = target.TargetId == Guid.Empty ? Guid.NewGuid() : target.TargetId,
                Name = target.Name,
                Kind = Parse(target.Kind, CaptureTargetKind.Window),
                Enabled = target.Enabled,
                DisabledReasonCode = target.Enabled
                    ? null
                    : preservedTarget?.DisabledReasonCode,
                DetectionLongEdge = target.DetectionLongEdge,
                ScanRemainingArea = target.ScanRemainingArea,
                RemainingAreaInterval =
                    TimeSpan.FromMilliseconds(target.RemainingAreaIntervalMilliseconds),
                RemainingAreaRegion = remainingArea,
                Regions = regions,
            };
        }).ToList();
        return existing with
        {
            Name = draft.Name,
            SourceLanguage = draft.SourceLanguage,
            TargetLanguage = draft.TargetLanguage,
            Context = existing.Context with
            {
                GameName = draft.GameName,
                GameDescription = draft.GameDescription,
                RecentLineCount = draft.RecentLineCount,
            },
            Targets = targets,
        };
    }

    private static T Parse<T>(string value, T fallback) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out T parsed) ? parsed : fallback;

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
