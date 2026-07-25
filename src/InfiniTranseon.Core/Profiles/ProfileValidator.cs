using InfiniTranseon.Contracts.Runtime;
using System.Globalization;

namespace InfiniTranseon.Core.Profiles;

public enum ProfileIssueSeverity
{
    Warning,
    Error,
}

public sealed record ProfileValidationIssue(
    ProfileIssueSeverity Severity,
    string Code,
    string Path);

public sealed record ProfileValidationResult(
    ProfileDocument Document,
    IReadOnlyList<ProfileValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != ProfileIssueSeverity.Error);
}

public sealed class ProfileValidator
{
    public ProfileValidationResult Validate(
        ProfileDocument source,
        RuntimeCapabilities capabilities,
        IReadOnlySet<string> knownProviderIds)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(knownProviderIds);
        ProfileDocument document = ProfileJson.Deserialize(ProfileJson.Serialize(source));
        var issues = new List<ProfileValidationIssue>();
        var identifiers = new HashSet<Guid>();

        ValidateIdentity(document.ProfileId, "$.profileId");
        RequireText(document.Name, "profile.name.required", "$.name");
        RequireText(document.SourceLanguage, "profile.language.sourceRequired", "$.sourceLanguage");
        RequireText(document.TargetLanguage, "profile.language.targetRequired", "$.targetLanguage");
        if (document.Context.RecentLineCount is < 0 or > 100)
        {
            Error("profile.context.recentLineCountInvalid", "$.context.recentLineCount");
        }
        if (document.History.Enabled &&
            (document.History.MaxAgeDays <= 0 || document.History.MaxBytes <= 0))
        {
            Error("profile.history.retentionInvalid", "$.history");
        }
        if (document.SchemaVersion != ProfileDocument.CurrentVersion)
        {
            Error("profile.schema.unsupported", "$.schemaVersion");
        }
        if (document.StylePrompt.Versions.Count > 32)
        {
            Error("profile.stylePrompt.tooManyVersions", "$.stylePrompt.versions");
        }
        if (document.StylePrompt.Versions
            .Select(version => version.Version)
            .Any(version => version <= 0) ||
            document.StylePrompt.Versions
                .Select(version => version.Version)
                .Distinct()
                .Count() != document.StylePrompt.Versions.Count)
        {
            Error("profile.stylePrompt.versionInvalid", "$.stylePrompt.versions");
        }
        foreach (ProfileStylePromptVersion version in document.StylePrompt.Versions)
        {
            if (string.IsNullOrWhiteSpace(version.Name) || version.Name.Length > 128 ||
                string.IsNullOrWhiteSpace(version.Template) || version.Template.Length > 8_192)
            {
                Error(
                    "profile.stylePrompt.contentInvalid",
                    $"$.stylePrompt.versions[{version.Version}]");
            }
        }
        if (document.StylePrompt.ActiveVersion != 0 &&
            document.StylePrompt.Active is null)
        {
            Error("profile.stylePrompt.activeVersionUnknown", "$.stylePrompt.activeVersion");
        }

        for (int targetIndex = 0; targetIndex < document.Targets.Count; targetIndex++)
        {
            ProfileTarget target = document.Targets[targetIndex];
            string targetPath = $"$.targets[{targetIndex}]";
            ValidateIdentity(target.TargetId, targetPath + ".targetId");
            RequireText(target.Name, "profile.target.nameRequired", targetPath + ".name");
            if (targetIndex >= capabilities.MaxTargets)
            {
                DisableTarget(target, "profile.limit.targets", targetPath);
            }
            if (target.RemainingAreaInterval <= TimeSpan.Zero)
            {
                Error("profile.target.intervalInvalid", targetPath + ".remainingAreaInterval");
            }
            if (target.DetectionLongEdge is < 320 or > 1920)
            {
                Error("profile.target.detectionLongEdgeInvalid", targetPath + ".detectionLongEdge");
            }
            bool validDesktopRegion = target.DesktopRegion is
            {
                Width: > 0 and <= 16_384,
                Height: > 0 and <= 16_384,
            } desktopRegion &&
                (long)desktopRegion.X + desktopRegion.Width <= int.MaxValue &&
                (long)desktopRegion.Y + desktopRegion.Height <= int.MaxValue;
            if (target.Kind == CaptureTargetKind.DesktopFixedRegion && !validDesktopRegion)
            {
                Error("profile.target.desktopRegionRequired", targetPath + ".desktopRegion");
            }
            if (target.Kind != CaptureTargetKind.DesktopFixedRegion && target.DesktopRegion is not null)
            {
                Error("profile.target.desktopRegionUnexpected", targetPath + ".desktopRegion");
            }
            if (target.RemainingAreaInterval.TotalMilliseconds > 3_600_000)
            {
                Error("profile.target.intervalInvalid", targetPath + ".remainingAreaInterval");
            }
            for (int variantIndex = 0; variantIndex < target.LayoutVariants.Count; variantIndex++)
            {
                ProfileLayoutVariant variant = target.LayoutVariants[variantIndex];
                string variantPath = $"{targetPath}.layoutVariants[{variantIndex}]";
                ValidateIdentity(variant.VariantId, variantPath + ".variantId");
                RequireText(variant.Name, "profile.layout.nameRequired", variantPath + ".name");
                if (!double.IsFinite(variant.MinimumAspectRatio) ||
                    !double.IsFinite(variant.MaximumAspectRatio) ||
                    !double.IsFinite(variant.BoundsScale) ||
                    variant.MinimumAspectRatio <= 0 ||
                    variant.MaximumAspectRatio < variant.MinimumAspectRatio ||
                    variant.BoundsScale <= 0 ||
                    variant.MinimumWidthPixels <= 0 ||
                    variant.MaximumWidthPixels <= 0 ||
                    variant.MinimumWidthPixels > variant.MaximumWidthPixels)
                {
                    Error("profile.layout.rangeInvalid", variantPath);
                }
            }

            if (target.ScanRemainingArea && target.RemainingAreaRegion is null)
                Error("profile.target.remainingAreaSettingsRequired", targetPath + ".remainingAreaRegion");
            if (target.RemainingAreaRegion is not null &&
                target.RemainingAreaRegion.AreaMode != CaptureAreaKind.RemainingArea)
                Error("profile.target.remainingAreaModeInvalid", targetPath + ".remainingAreaRegion.areaMode");
            if (target.Regions.Any(region => region.AreaMode == CaptureAreaKind.RemainingArea))
                Error("profile.target.remainingAreaLocationInvalid", targetPath + ".regions");

            var regionsToValidate = target.Regions
                .Select((region, index) => (
                    Region: region,
                    Path: $"{targetPath}.regions[{index}]"))
                .ToList();
            if (target.RemainingAreaRegion is not null)
                regionsToValidate.Add((
                    target.RemainingAreaRegion,
                    targetPath + ".remainingAreaRegion"));
            for (int regionIndex = 0; regionIndex < regionsToValidate.Count; regionIndex++)
            {
                ProfileRegion region = regionsToValidate[regionIndex].Region;
                string regionPath = regionsToValidate[regionIndex].Path;
                ValidateIdentity(region.RegionId, regionPath + ".regionId");
                RequireText(region.Name, "profile.region.nameRequired", regionPath + ".name");
                if (regionIndex >= capabilities.MaxRegionsPerTarget)
                {
                    DisableRegion(region, "profile.limit.regions", regionPath);
                }
                if (region.RecognitionInterval.TotalMilliseconds is < 16 or > 3_600_000)
                {
                    Error("profile.region.intervalInvalid", regionPath + ".recognitionInterval");
                }
                RequireText(region.Ocr.ProviderId, "profile.ocr.providerRequired", regionPath + ".ocr.providerId");
                if (!double.IsFinite(region.Ocr.DetectionScale) || region.Ocr.DetectionScale is < 0.1 or > 1)
                {
                    Error("profile.ocr.detectionScaleInvalid", regionPath + ".ocr.detectionScale");
                }
                if (region.Ocr.PreprocessingSteps.Count > 16 ||
                    region.Ocr.PreprocessingSteps.Any(step => string.IsNullOrWhiteSpace(step) ||
                        step.Any(char.IsControl) || !IsValidPreprocessingStep(step)))
                {
                    Error("profile.ocr.preprocessingInvalid", regionPath + ".ocr.preprocessingSteps");
                }
                if (document.StrictOffline && region.Ocr.UseCloudOcr)
                {
                    Error("profile.offline.cloudOcr", regionPath + ".ocr.useCloudOcr");
                }
                if (region.Ocr.CloudConsentPolicyRevision < 0 ||
                    region.Ocr.UseCloudOcr != (region.Ocr.CloudConsentPolicyRevision > 0))
                {
                    Error("profile.ocr.cloudConsentInvalid", regionPath + ".ocr.cloudConsentPolicyRevision");
                }
                if (!double.IsFinite(region.Overlay.Opacity) || region.Overlay.Opacity is < 0 or > 1 ||
                    !double.IsFinite(region.Overlay.BlurRadius) ||
                    region.Overlay.BlurRadius is < 0 or > 64 ||
                    !double.IsFinite(region.Overlay.OutlineWidth) ||
                    region.Overlay.OutlineWidth is < 0 or > 8 ||
                    !double.IsFinite(region.Overlay.PreferredFontSize) ||
                    region.Overlay.PreferredFontSize is < 12 or > 36 ||
                    region.Overlay.MinimumDwell < TimeSpan.Zero ||
                    region.Overlay.MinimumDwell > TimeSpan.FromSeconds(3) ||
                    region.Overlay.CrossfadeDuration < TimeSpan.Zero ||
                    region.Overlay.CrossfadeDuration > TimeSpan.FromMilliseconds(500) ||
                    !IsOptionalArgb(region.Overlay.BackgroundColor) ||
                    !IsOptionalArgb(region.Overlay.TextColor) ||
                    !IsOptionalArgb(region.Overlay.OutlineColor))
                {
                    Error("profile.overlay.styleInvalid", regionPath + ".overlay");
                }
                if (region.LineBreakMode == LineBreakMode.CustomSeparator &&
                    region.CustomLineSeparator is null)
                {
                    Error("profile.lineBreak.separatorRequired", regionPath + ".customLineSeparator");
                }
                if (region.MaximumLines is < 1 ||
                    region.MaximumLines > capabilities.MaxOcrBoxesPerResult)
                {
                    Error("profile.lineBreak.maximumLinesInvalid", regionPath + ".maximumLines");
                }

                for (int channelIndex = 0; channelIndex < region.TranslationChannels.Count; channelIndex++)
                {
                    ProfileTranslationChannel channel = region.TranslationChannels[channelIndex];
                    string channelPath = $"{regionPath}.translationChannels[{channelIndex}]";
                    ValidateIdentity(channel.ChannelId, channelPath + ".channelId");
                    if (channelIndex >= capabilities.MaxTranslationChannelsPerRegion)
                    {
                        DisableChannel(channel, "profile.limit.translationChannels", channelPath);
                    }
                    RequireText(channel.InitialProviderId, "profile.provider.required", channelPath + ".initialProviderId");
                    if (!string.IsNullOrWhiteSpace(channel.InitialProviderId) &&
                        !knownProviderIds.Contains(channel.InitialProviderId))
                    {
                        Error("profile.provider.unknown", channelPath + ".initialProviderId");
                    }
                    if (channel.FallbackProviderIds.Count > 2)
                    {
                        Error("profile.limit.fallbacks", channelPath + ".fallbackProviderIds");
                    }
                    if (channel.FallbackProviderIds.Distinct(StringComparer.Ordinal).Count() !=
                        channel.FallbackProviderIds.Count)
                    {
                        Error("profile.provider.duplicateFallback", channelPath + ".fallbackProviderIds");
                    }
                    foreach (string fallbackProviderId in channel.FallbackProviderIds)
                    {
                        if (!knownProviderIds.Contains(fallbackProviderId))
                        {
                            Error("profile.provider.unknown", channelPath + ".fallbackProviderIds");
                        }
                    }
                    if (channel.RefinementSteps.Count > 2)
                    {
                        Error("profile.limit.refinements", channelPath + ".refinementSteps");
                    }
                    if (channel.RetryCount is < 0 or > 1)
                    {
                        Error("profile.limit.retry", channelPath + ".retryCount");
                    }
                    if (channel.MaxEstimatedCostPerRequest < 0)
                    {
                        Error("profile.provider.budgetInvalid", channelPath + ".maxEstimatedCostPerRequest");
                    }
                    if (document.StrictOffline && channel.NetworkPolicy == ProviderNetworkPolicy.OnlineOnly)
                    {
                        Error("profile.offline.onlineProvider", channelPath + ".networkPolicy");
                    }
                    foreach (ProfileRefinementStep step in channel.RefinementSteps)
                    {
                        ValidateIdentity(step.StageId, channelPath + ".refinementSteps[].stageId");
                        if (!knownProviderIds.Contains(step.ProviderId))
                        {
                            Error("profile.provider.unknown", channelPath + ".refinementSteps[].providerId");
                        }
                    }
                }

                if (region.TranslationEnabled &&
                    !region.TranslationChannels.Any(channel => channel.Enabled))
                {
                    Error("profile.translation.enabledWithoutChannel", regionPath + ".translationChannels");
                }
            }

            HashSet<Guid> regionIds = target.Regions.Select(region => region.RegionId).ToHashSet();
            for (int variantIndex = 0; variantIndex < target.LayoutVariants.Count; variantIndex++)
            {
                ProfileLayoutVariant variant = target.LayoutVariants[variantIndex];
                foreach (Guid regionId in variant.RegionBounds.Keys)
                {
                    if (!regionIds.Contains(regionId))
                    {
                        Error(
                            "profile.layout.regionUnknown",
                            $"{targetPath}.layoutVariants[{variantIndex}].regionBounds");
                    }
                }
            }
        }

        foreach (ProfileHotkey hotkey in document.Hotkeys)
        {
            ValidateIdentity(hotkey.HotkeyId, "$.hotkeys[].hotkeyId");
        }

        return new ProfileValidationResult(document, issues.AsReadOnly());

        void ValidateIdentity(Guid id, string path)
        {
            if (id == Guid.Empty) Error("profile.id.empty", path);
            else if (!identifiers.Add(id)) Error("profile.id.duplicate", path);
        }

        void RequireText(string value, string code, string path)
        {
            if (string.IsNullOrWhiteSpace(value)) Error(code, path);
        }

        void Error(string code, string path) =>
            issues.Add(new ProfileValidationIssue(ProfileIssueSeverity.Error, code, path));

        void Warning(string code, string path) =>
            issues.Add(new ProfileValidationIssue(ProfileIssueSeverity.Warning, code, path));

        void DisableTarget(ProfileTarget item, string code, string path)
        {
            item.Enabled = false;
            item.DisabledReasonCode = code;
            Warning(code, path);
        }

        void DisableRegion(ProfileRegion item, string code, string path)
        {
            item.Enabled = false;
            item.DisabledReasonCode = code;
            Warning(code, path);
        }

        void DisableChannel(ProfileTranslationChannel item, string code, string path)
        {
            item.Enabled = false;
            item.DisabledReasonCode = code;
            Warning(code, path);
        }

        static bool IsValidPreprocessingStep(string step)
        {
            if (step is "grayscale" or "threshold" or "adaptive-threshold" or
                "sharpen" or "outline-suppression" or "invert" or "alpha-cleanup")
                return true;
            if (TrySuffix("contrast:", out string contrast))
                return double.TryParse(contrast, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double contrastValue) && double.IsFinite(contrastValue) &&
                    contrastValue is >= 0.1 and <= 4;
            if (TrySuffix("adaptive-threshold:", out string radius))
                return uint.TryParse(radius, NumberStyles.None, CultureInfo.InvariantCulture,
                    out uint radiusValue) && radiusValue is >= 1 and <= 64;
            if (TrySuffix("scale:", out string scale))
                return uint.TryParse(scale, NumberStyles.None, CultureInfo.InvariantCulture,
                    out uint scaleValue) && scaleValue is >= 1 and <= 8;
            if (TrySuffix("alpha-cleanup:", out string alpha))
                return byte.TryParse(alpha, NumberStyles.None, CultureInfo.InvariantCulture, out _);
            const string colorPrefix = "color-isolation:#";
            if (step.StartsWith(colorPrefix, StringComparison.Ordinal))
            {
                string value = step[colorPrefix.Length..];
                return value.Length >= 8 && value[6] == ':' &&
                    uint.TryParse(value[..6], NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out _) &&
                    byte.TryParse(value[7..], NumberStyles.None,
                        CultureInfo.InvariantCulture, out _);
            }
            return false;

            bool TrySuffix(string prefix, out string suffix)
            {
                bool matches = step.StartsWith(prefix, StringComparison.Ordinal);
                suffix = matches ? step[prefix.Length..] : string.Empty;
                return matches;
            }
        }
    }

    private static bool IsOptionalArgb(string? value) => value is null ||
        value is { Length: 9 } && value[0] == '#' &&
        value.AsSpan(1).ToString().All(char.IsAsciiHexDigit);
}
