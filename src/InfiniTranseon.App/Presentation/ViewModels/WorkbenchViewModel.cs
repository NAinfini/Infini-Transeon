using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Presentation.ViewModels;

public sealed partial class WorkbenchViewModel : PageViewModelBase
{
    private readonly IWorkbenchService _service;
    private readonly Stack<WorkbenchProfileDraft> _undo = [];
    private readonly Stack<WorkbenchProfileDraft> _redo = [];
    private Guid _profileId;
    private bool _isLoaded;

    public WorkbenchViewModel(
        IWorkbenchService service,
        RuntimeCapabilitiesService? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        MaxTranslationChannels =
            (capabilities?.Capabilities ?? RuntimeCapabilities.VersionOne)
            .MaxTranslationChannelsPerRegion;
    }

    public ObservableCollection<WorkbenchTargetItem> Targets { get; } = [];

    [ObservableProperty]
    public partial WorkbenchTargetItem? SelectedTarget { get; set; }

    [ObservableProperty]
    public partial WorkbenchRegionItem? SelectedRegion { get; set; }

    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceLanguage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TargetLanguage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GameName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GameDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int RecentLineCount { get; set; } = 6;

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    public partial string ApplyState { get; private set; } = string.Empty;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int MaxTranslationChannels { get; }

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _profileId == Guid.Empty
            ? Task.CompletedTask
            : LoadAsync(_profileId, cancellationToken);

    /// <summary>
    /// Loads the profile only when this instance is not already holding its draft. The five workspace
    /// sections edit one shared draft, so re-entering a section must not reload over unsaved edits;
    /// an unconditional <see cref="LoadAsync"/> per section would discard them without telling anyone.
    /// </summary>
    public Task EnsureLoadedAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _isLoaded && _profileId == profileId
            ? Task.CompletedTask
            : LoadAsync(profileId, cancellationToken);

    public Task LoadAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        _profileId = profileId;
        return RunGuardedAsync(async () =>
        {
            WorkbenchProfileDraft? draft = await _service
                .LoadAsync(profileId, cancellationToken)
                .ConfigureAwait(true);
            if (draft is null)
                throw new KeyNotFoundException("Profile was not found.");
            Restore(draft);
            _undo.Clear();
            _redo.Clear();
            IsDirty = false;
            ApplyState = string.Empty;
            IsEmpty = Targets.Count == 0;
            _isLoaded = true;
            NotifyHistoryChanged();
        });
    }

    /// <summary>
    /// Drops unsaved edits by reloading the stored profile. Reloading rather than just clearing the
    /// dirty flag is deliberate: the discarded edits must leave memory, or a later save would write
    /// changes the user already chose to throw away.
    /// </summary>
    public Task DiscardChangesAsync(CancellationToken cancellationToken = default) =>
        _isLoaded && _profileId != Guid.Empty
            ? LoadAsync(_profileId, cancellationToken)
            : Task.CompletedTask;

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            ValidateForSave();
            ProfileRuntimeApplyResult result = await _service
                .SaveAndApplyAsync(Snapshot(), cancellationToken)
                .ConfigureAwait(true);
            IsDirty = false;
            ApplyState = result.ToString();
        });

    public void AddRegion()
    {
        WorkbenchTargetItem? target = SelectedTarget;
        if (target is null) return;
        PushUndo();
        WorkbenchRegionItem? template = SelectedRegion;
        var region = WorkbenchRegionItem.CreateDefault(
            $"Region {target.Regions.Count + 1}");
        if (template is not null && template.Channels.Count > 0)
        {
            region.Channels.AddRange(template.Channels.Select(channel => channel with
            {
                ChannelId = Guid.NewGuid(),
            }));
            region.TranslationEnabled = template.TranslationEnabled;
        }
        target.Regions.Add(region);
        SelectedRegion = region;
        MarkDirty();
    }

    public void DuplicateSelectedRegion()
    {
        if (SelectedTarget is not { } target || SelectedRegion is not { } selected)
            return;
        PushUndo();
        int index = target.Regions.IndexOf(selected);
        WorkbenchRegionItem duplicate = selected.Duplicate();
        target.Regions.Insert(index + 1, duplicate);
        SelectedRegion = duplicate;
        MarkDirty();
    }

    public void DeleteSelectedRegion()
    {
        if (SelectedTarget is not { } target || SelectedRegion is not { } selected)
            return;
        PushUndo();
        int index = target.Regions.IndexOf(selected);
        target.Regions.Remove(selected);
        SelectedRegion = target.Regions.Count == 0
            ? null
            : target.Regions[Math.Clamp(index, 0, target.Regions.Count - 1)];
        MarkDirty();
    }

    public void MoveSelectedRegion(int delta)
    {
        if (SelectedTarget is not { } target || SelectedRegion is not { } selected)
            return;
        int from = target.Regions.IndexOf(selected);
        int to = Math.Clamp(from + delta, 0, target.Regions.Count - 1);
        if (from == to) return;
        PushUndo();
        target.Regions.Move(from, to);
        MarkDirty();
    }

    public void SetRegionEnabled(WorkbenchRegionItem region, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.Enabled == enabled) return;
        PushUndo();
        region.Enabled = enabled;
        MarkDirty();
    }

    public void SetRegionBounds(
        WorkbenchRegionItem region,
        double x,
        double y,
        double width,
        double height,
        bool createUndoPoint)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (createUndoPoint) PushUndo();
        const double minimum = 0.01;
        width = Math.Clamp(width, minimum, 1);
        height = Math.Clamp(height, minimum, 1);
        x = Math.Clamp(x, 0, 1 - width);
        y = Math.Clamp(y, 0, 1 - height);
        region.X = x;
        region.Y = y;
        region.Width = width;
        region.Height = height;
        MarkDirty();
    }

    public void MarkEditorChanged(bool createUndoPoint = false)
    {
        if (createUndoPoint) PushUndo();
        MarkDirty();
    }

    public bool TryAddChannel(WorkbenchRegionItem region, WorkbenchChannelDraft channel)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(channel);
        if (region.Channels.Count >= MaxTranslationChannels)
        {
            return false;
        }
        PushUndo();
        region.Channels.Add(channel with { DisplayOrder = region.Channels.Count });
        MarkDirty();
        return true;
    }

    public bool TryUpdateChannel(
        WorkbenchRegionItem region,
        WorkbenchChannelDraft current,
        WorkbenchChannelDraft updated)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(updated);
        int index = region.Channels.IndexOf(current);
        if (index < 0)
        {
            return false;
        }
        PushUndo();
        region.Channels[index] = updated with { DisplayOrder = index };
        MarkDirty();
        return true;
    }

    public bool MoveChannel(
        WorkbenchRegionItem region,
        WorkbenchChannelDraft channel,
        int delta)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(channel);
        int index = region.Channels.IndexOf(channel);
        int destination = index + delta;
        if (index < 0 || destination < 0 || destination >= region.Channels.Count)
        {
            return false;
        }
        PushUndo();
        region.Channels.RemoveAt(index);
        region.Channels.Insert(destination, channel);
        NormalizeChannelOrder(region);
        MarkDirty();
        return true;
    }

    public bool RemoveChannel(
        WorkbenchRegionItem region,
        WorkbenchChannelDraft channel)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(channel);
        int index = region.Channels.IndexOf(channel);
        if (index < 0)
        {
            return false;
        }
        PushUndo();
        region.Channels.RemoveAt(index);
        NormalizeChannelOrder(region);
        MarkDirty();
        return true;
    }

    public void Undo()
    {
        if (!_undo.TryPop(out WorkbenchProfileDraft? previous)) return;
        _redo.Push(Snapshot());
        Restore(previous);
        IsDirty = true;
        NotifyHistoryChanged();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out WorkbenchProfileDraft? next)) return;
        _undo.Push(Snapshot());
        Restore(next);
        IsDirty = true;
        NotifyHistoryChanged();
    }

    private void PushUndo()
    {
        _undo.Push(Snapshot());
        _redo.Clear();
        NotifyHistoryChanged();
    }

    private void MarkDirty()
    {
        IsDirty = true;
        ApplyState = string.Empty;
    }

    private WorkbenchProfileDraft Snapshot() => new(
        _profileId,
        ProfileName.Trim(),
        SourceLanguage.Trim(),
        TargetLanguage.Trim(),
        GameName.Trim(),
        GameDescription.Trim(),
        RecentLineCount,
        Targets.Select(target => target.ToDraft()).ToArray());

    private void Restore(WorkbenchProfileDraft draft)
    {
        Guid? targetId = SelectedTarget?.TargetId;
        Guid? regionId = SelectedRegion?.RegionId;
        _profileId = draft.ProfileId;
        ProfileName = draft.Name;
        SourceLanguage = draft.SourceLanguage;
        TargetLanguage = draft.TargetLanguage;
        GameName = draft.GameName;
        GameDescription = draft.GameDescription;
        RecentLineCount = draft.RecentLineCount;
        Targets.Clear();
        foreach (WorkbenchTargetDraft target in draft.Targets)
            Targets.Add(new WorkbenchTargetItem(target));
        SelectedTarget = Targets.FirstOrDefault(target => target.TargetId == targetId)
            ?? Targets.FirstOrDefault();
        SelectedRegion = SelectedTarget?.Regions
            .FirstOrDefault(region => region.RegionId == regionId)
            ?? SelectedTarget?.Regions.FirstOrDefault();
    }

    private void ValidateForSave()
    {
        if (string.IsNullOrWhiteSpace(ProfileName) ||
            string.IsNullOrWhiteSpace(SourceLanguage) ||
            string.IsNullOrWhiteSpace(TargetLanguage))
            throw new InvalidOperationException("Profile name and languages are required.");
        // Enforced here rather than only in the editors: a profile whose source equals its target
        // would translate into itself, and every save path must reject it, not just the one the
        // creation wizard uses.
        if (string.Equals(SourceLanguage.Trim(), TargetLanguage.Trim(), StringComparison.CurrentCultureIgnoreCase))
            throw new InvalidOperationException(
                "Source and target languages must differ.");
        if (Targets.Count == 0)
            throw new InvalidOperationException("At least one capture target is required.");
        if (Targets.Any(target => target.Regions.Count == 0 && !target.ScanRemainingArea))
            throw new InvalidOperationException(
                "Each target needs a region or remaining-area scanning.");
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private static void NormalizeChannelOrder(WorkbenchRegionItem region)
    {
        for (int index = 0; index < region.Channels.Count; index++)
        {
            region.Channels[index] = region.Channels[index] with { DisplayOrder = index };
        }
    }

    /// <summary>
    /// Builds the deduplicated refiner provider id list for a channel's pipeline card: empty
    /// selections are dropped, and a refiner cannot equal the channel's initial provider or the
    /// other selected refiner (spec 5.5 "禁止与初始同提供商").
    /// </summary>
    public static IReadOnlyList<string> BuildRefinerIds(
        string initialProviderId,
        string? firstRefinerId,
        string? secondRefinerId) =>
        new[] { firstRefinerId, secondRefinerId }
            .Where(id => !string.IsNullOrEmpty(id) &&
                !string.Equals(id, initialProviderId, StringComparison.Ordinal))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

public sealed partial class WorkbenchTargetItem : ObservableObject
{
    public WorkbenchTargetItem(WorkbenchTargetDraft draft)
    {
        TargetId = draft.TargetId;
        Name = draft.Name;
        Kind = draft.Kind;
        Enabled = draft.Enabled;
        DetectionLongEdge = draft.DetectionLongEdge;
        ScanRemainingArea = draft.ScanRemainingArea;
        RemainingAreaIntervalMilliseconds = draft.RemainingAreaIntervalMilliseconds;
        foreach (WorkbenchRegionDraft region in draft.Regions)
            Regions.Add(new WorkbenchRegionItem(region));
    }

    public Guid TargetId { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Kind { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial int DetectionLongEdge { get; set; }

    [ObservableProperty]
    public partial bool ScanRemainingArea { get; set; }

    [ObservableProperty]
    public partial int RemainingAreaIntervalMilliseconds { get; set; }

    public ObservableCollection<WorkbenchRegionItem> Regions { get; } = [];

    public WorkbenchTargetDraft ToDraft() => new(
        TargetId,
        Name.Trim(),
        Kind,
        Enabled,
        DetectionLongEdge,
        ScanRemainingArea,
        RemainingAreaIntervalMilliseconds,
        Regions.Select(region => region.ToDraft()).ToArray());

    public override string ToString() => $"{Name} ({Kind})";
}

public sealed partial class WorkbenchRegionItem : ObservableObject
{
    public WorkbenchRegionItem(WorkbenchRegionDraft draft)
    {
        RegionId = draft.RegionId;
        Name = draft.Name;
        Enabled = draft.Enabled;
        X = draft.X;
        Y = draft.Y;
        Width = draft.Width;
        Height = draft.Height;
        Priority = draft.Priority;
        RecognitionIntervalMilliseconds = draft.RecognitionIntervalMilliseconds;
        OcrProviderId = draft.OcrProviderId;
        RecognitionLanguage = draft.RecognitionLanguage;
        DetectionScale = draft.DetectionScale;
        DetectOrientation = draft.DetectOrientation;
        UseCloudOcr = draft.UseCloudOcr;
        CloudConsentPolicyRevision = draft.CloudConsentPolicyRevision;
        TranslationEnabled = draft.TranslationEnabled;
        Channels = draft.Channels.ToList();
        LineBreakMode = draft.LineBreakMode;
        CustomLineSeparator = draft.CustomLineSeparator;
        MaximumLines = draft.MaximumLines;
        LineAlignment = draft.LineAlignment;
        ContextRole = draft.ContextRole;
        OverlayMode = draft.OverlayMode;
        OverlayBackgroundMode = draft.OverlayBackgroundMode;
        BackgroundColor = draft.BackgroundColor;
        TextColor = draft.TextColor;
        OverlayOpacity = draft.OverlayOpacity;
        BlurRadius = draft.BlurRadius;
        PreferredFontSize = draft.PreferredFontSize;
        OutlineColor = draft.OutlineColor;
        OutlineWidth = draft.OutlineWidth;
        LockDegradation = draft.LockDegradation;
    }

    public Guid RegionId { get; }
    public List<WorkbenchChannelDraft> Channels { get; }

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial double X { get; set; }
    [ObservableProperty] public partial double Y { get; set; }
    [ObservableProperty] public partial double Width { get; set; }
    [ObservableProperty] public partial double Height { get; set; }
    [ObservableProperty] public partial RegionPriorityLevel Priority { get; set; }
    [ObservableProperty] public partial int RecognitionIntervalMilliseconds { get; set; }
    [ObservableProperty] public partial string OcrProviderId { get; set; }
    [ObservableProperty] public partial string RecognitionLanguage { get; set; }
    [ObservableProperty] public partial double DetectionScale { get; set; }
    [ObservableProperty] public partial bool DetectOrientation { get; set; }
    [ObservableProperty] public partial bool UseCloudOcr { get; set; }
    [ObservableProperty] public partial long CloudConsentPolicyRevision { get; set; }
    [ObservableProperty] public partial bool TranslationEnabled { get; set; }
    [ObservableProperty] public partial string LineBreakMode { get; set; }
    [ObservableProperty] public partial string? CustomLineSeparator { get; set; }
    [ObservableProperty] public partial int MaximumLines { get; set; }
    [ObservableProperty] public partial string LineAlignment { get; set; }
    [ObservableProperty] public partial RegionContextRole ContextRole { get; set; }
    [ObservableProperty] public partial string OverlayMode { get; set; }
    [ObservableProperty] public partial string OverlayBackgroundMode { get; set; }
    [ObservableProperty] public partial string? BackgroundColor { get; set; }
    [ObservableProperty] public partial string? TextColor { get; set; }
    [ObservableProperty] public partial double OverlayOpacity { get; set; }
    [ObservableProperty] public partial double BlurRadius { get; set; }
    [ObservableProperty] public partial double PreferredFontSize { get; set; }
    [ObservableProperty] public partial string? OutlineColor { get; set; }
    [ObservableProperty] public partial double OutlineWidth { get; set; }
    [ObservableProperty] public partial bool LockDegradation { get; set; }

    public WorkbenchRegionDraft ToDraft() => new(
        RegionId,
        Name.Trim(),
        Enabled,
        X,
        Y,
        Width,
        Height,
        Priority,
        RecognitionIntervalMilliseconds,
        OcrProviderId.Trim(),
        RecognitionLanguage.Trim(),
        DetectionScale,
        DetectOrientation,
        UseCloudOcr,
        CloudConsentPolicyRevision,
        TranslationEnabled,
        Channels.ToArray(),
        LineBreakMode,
        CustomLineSeparator,
        MaximumLines,
        LineAlignment,
        ContextRole,
        OverlayMode,
        OverlayBackgroundMode,
        BackgroundColor,
        TextColor,
        OverlayOpacity,
        BlurRadius,
        LockDegradation,
        PreferredFontSize,
        OutlineColor,
        OutlineWidth);

    public WorkbenchRegionItem Duplicate() => new(ToDraft() with
    {
        RegionId = Guid.NewGuid(),
        Name = Name + " copy",
        X = Math.Min(X + 0.02, 1 - Width),
        Y = Math.Min(Y + 0.02, 1 - Height),
        Channels = Channels.Select(channel => channel with
        {
            ChannelId = Guid.NewGuid(),
        }).ToArray(),
    });

    public static WorkbenchRegionItem CreateDefault(string name) => new(
        new WorkbenchRegionDraft(
            Guid.NewGuid(),
            name,
            true,
            0.2,
            0.65,
            0.6,
            0.2,
            RegionPriorityLevel.P1,
            250,
            "auto",
            "auto",
            1,
            true,
            false,
            0,
            false,
            [],
            "PreserveLines",
            null,
            64,
            "Auto",
            RegionContextRole.None,
            "Replace",
            "AutomaticContrastBlur",
            null,
            null,
            0.85,
            12,
            false));

    public override string ToString() => $"{Name} · {Priority}";
}
