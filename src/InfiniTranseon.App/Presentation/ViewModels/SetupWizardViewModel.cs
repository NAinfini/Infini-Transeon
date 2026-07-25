using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Presentation.ViewModels;

/// <summary>Reason a step (or the Next action) is currently blocked. Pages map this to a
/// localized string; the view model never carries UI text (spec 9.4 "禁用但不解释" is a defect,
/// but the fix belongs in the page, not here — see <see cref="ViewModelArchitectureTests"/>).</summary>
public enum SetupStepGateReason
{
    None,
    NeedsProfileName,
    NeedsCaptureTarget,
    NeedsLanguages,
    NeedsProvider,
    NeedsRegions,
}

/// <summary>
/// Drives the four-step guided setup (design spec 5.2): Capture target → Languages &amp;
/// translator → Regions → Review &amp; save. Step 1 targets come from the capture probe, step 2
/// providers from the settings service (with secret entry writing straight through the secret
/// service) and a live translation probe test, step 3 reuses the exact <see cref="WorkbenchRegionItem"/>
/// model the profile workspace's region canvas already binds to (no parallel region-draft type),
/// and step 4 builds a presentation <see cref="ProfileEditModel"/> and persists it via the profile
/// service. Stays free of WinUI and Core types so it remains unit-testable.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    public const int StepCount = 4;

    private readonly ICaptureProbe _captureProbe;
    private readonly IOcrProbe _ocrProbe;
    private readonly ITranslationProbe _translationProbe;
    private readonly ISettingsService _settingsService;
    private readonly ISecretReferenceService _secrets;
    private readonly IProfileService _profileService;
    private readonly Dictionary<Guid, DesktopRegionState> _desktopRegionStates = [];
    private Guid _editingProfileId;
    private bool _isSynchronizingDesktopRegion;

    public SetupWizardViewModel(
        ICaptureProbe captureProbe,
        IOcrProbe ocrProbe,
        ITranslationProbe translationProbe,
        ISettingsService settingsService,
        ISecretReferenceService secrets,
        IProfileService profileService)
    {
        ArgumentNullException.ThrowIfNull(captureProbe);
        ArgumentNullException.ThrowIfNull(ocrProbe);
        ArgumentNullException.ThrowIfNull(translationProbe);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(profileService);
        _captureProbe = captureProbe;
        _ocrProbe = ocrProbe;
        _translationProbe = translationProbe;
        _settingsService = settingsService;
        _secrets = secrets;
        _profileService = profileService;
        Regions.CollectionChanged += OnRegionsCollectionChanged;
        BackCommand = new RelayCommand(GoBack, () => CanGoBack);
        NextCommand = new RelayCommand(GoNext, () => CanGoNext);
        SaveCommand = new AsyncRelayCommand(
            () => SaveAsync(allowDraft: false),
            () => CanSave);
        SaveDraftCommand = new AsyncRelayCommand(
            () => SaveAsync(allowDraft: true),
            () => CanSaveDraft);
        TestTranslationCommand = new AsyncRelayCommand(
            TestTranslationAsync,
            () => !IsTranslationTestBusy && HasReadyLanguages && SelectedProvider is not null);
        AddDefaultRegion();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStepNumber))]
    [NotifyPropertyChangedFor(nameof(IsStep1))]
    [NotifyPropertyChangedFor(nameof(IsStep2))]
    [NotifyPropertyChangedFor(nameof(IsStep3))]
    [NotifyPropertyChangedFor(nameof(IsStep4))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(CurrentStepGateReason))]
    [NotifyPropertyChangedFor(nameof(IsStep2Reachable))]
    [NotifyPropertyChangedFor(nameof(IsStep3Reachable))]
    [NotifyPropertyChangedFor(nameof(IsStep4Reachable))]
    public partial int CurrentStepIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadyTarget))]
    [NotifyPropertyChangedFor(nameof(CanUseDesktopFixedRegion))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationReady))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanSaveDraft))]
    [NotifyPropertyChangedFor(nameof(Step1Ready))]
    public partial CaptureProbeTarget? SelectedTarget { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadyTarget))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationReady))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanSaveDraft))]
    [NotifyPropertyChangedFor(nameof(Step1Ready))]
    public partial bool UseDesktopFixedRegion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadyTarget))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationReady))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanSaveDraft))]
    [NotifyPropertyChangedFor(nameof(Step1Ready))]
    public partial double DesktopRegionX { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadyTarget))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationReady))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanSaveDraft))]
    [NotifyPropertyChangedFor(nameof(Step1Ready))]
    public partial double DesktopRegionY { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadyTarget))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationReady))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanSaveDraft))]
    [NotifyPropertyChangedFor(nameof(Step1Ready))]
    public partial double DesktopRegionWidth { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadyTarget))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationReady))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanSaveDraft))]
    [NotifyPropertyChangedFor(nameof(Step1Ready))]
    public partial double DesktopRegionHeight { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadyProvider))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationReady))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(Step2Ready))]
    public partial ProviderRow? SelectedProvider { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanSaveDraft))]
    [NotifyPropertyChangedFor(nameof(Step1Ready))]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadyLanguages))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationReady))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(Step2Ready))]
    public partial string SourceLanguage { get; set; } = "auto";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadyLanguages))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationReady))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(Step2Ready))]
    public partial string TargetLanguage { get; set; } = "zh-Hans";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ObservableProperty]
    public partial Guid SavedProfileId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDraftSaved))]
    public partial bool WasSavedAsDraft { get; private set; }

    [ObservableProperty]
    public partial WorkbenchRegionItem? SelectedRegion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteTestTranslation))]
    public partial bool IsTranslationTestBusy { get; set; }

    [ObservableProperty]
    public partial string TranslationTestResult { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TranslationTestError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TimeSpan TranslationTestLatency { get; set; }

    public ObservableCollection<CaptureProbeTarget> Targets { get; } = [];

    public ObservableCollection<CaptureProbeTarget> SelectedTargets { get; } = [];

    public ObservableCollection<ProviderRow> Providers { get; } = [];

    /// <summary>
    /// The region draft list, typed as the workspace's own <see cref="WorkbenchRegionItem"/> so the
    /// region canvas control — which binds to exactly this type — can be reused verbatim in step 3
    /// (design spec 5.2 "复用工作区画布组件"). Persistence still goes through
    /// <see cref="IProfileService"/>'s lighter <c>ProfileRegionDraft</c> shape; <see cref="SaveAsync"/>
    /// projects the fields that shape understands (name, bounds, priority, context role).
    /// </summary>
    public ObservableCollection<WorkbenchRegionItem> Regions { get; } = [];

    public IRelayCommand BackCommand { get; }

    public IRelayCommand NextCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand SaveDraftCommand { get; }

    public IAsyncRelayCommand TestTranslationCommand { get; }

    public int CurrentStepNumber => CurrentStepIndex + 1;

    public bool IsStep1 => CurrentStepIndex == 0;

    public bool IsStep2 => CurrentStepIndex == 1;

    public bool IsStep3 => CurrentStepIndex == 2;

    public bool IsStep4 => CurrentStepIndex == 3;

    public bool CanGoBack => CurrentStepIndex > 0;

    public bool CanGoNext => CurrentStepIndex < StepCount - 1 && CurrentStepGateReason == SetupStepGateReason.None;

    public bool IsLastStep => CurrentStepIndex == StepCount - 1;

    public bool IsEditing => _editingProfileId != Guid.Empty;

    public bool CanUseDesktopFixedRegion =>
        SelectedTarget is not null &&
        (string.Equals(SelectedTarget.Kind, "Monitor", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(SelectedTarget.Kind, "Display", StringComparison.OrdinalIgnoreCase));

    public bool HasReadyTarget =>
        SelectedTargets.Count > 0 &&
        SelectedTargets.All(target => target.Capturable) &&
        SelectedTargets.All(HasValidTargetState);

    public bool HasReadyProvider => SelectedProvider?.IsReady == true;

    public bool HasReadyLanguages =>
        !string.IsNullOrWhiteSpace(SourceLanguage) &&
        !string.IsNullOrWhiteSpace(TargetLanguage) &&
        !string.Equals(SourceLanguage, TargetLanguage, StringComparison.OrdinalIgnoreCase);

    public bool HasReadyRegions =>
        Regions.Count > 0 &&
        Regions.All(region =>
            !string.IsNullOrWhiteSpace(region.Name) &&
            region.X is >= 0 and <= 1 &&
            region.Y is >= 0 and <= 1 &&
            region.Width is > 0 and <= 1 &&
            region.Height is > 0 and <= 1 &&
            region.X + region.Width <= 1 &&
            region.Y + region.Height <= 1);

    public bool IsConfigurationReady =>
        HasReadyTarget && HasReadyProvider && HasReadyLanguages && HasReadyRegions;

    public bool CanSaveDraft => !IsBusy && !string.IsNullOrWhiteSpace(ProfileName);

    public bool CanSave => CanSaveDraft && IsConfigurationReady;

    public bool IsDraftSaved => SavedProfileId != Guid.Empty && WasSavedAsDraft;

    public bool CanExecuteTestTranslation =>
        !IsTranslationTestBusy && HasReadyLanguages && SelectedProvider is not null;

    /// <summary>Step 1 gate: a named profile with at least one ready capture target.</summary>
    public bool Step1Ready => HasReadyTarget && !string.IsNullOrWhiteSpace(ProfileName);

    /// <summary>Step 2 gate: valid distinct languages plus a translator whose credentials are ready.</summary>
    public bool Step2Ready => HasReadyLanguages && HasReadyProvider;

    /// <summary>Step 3 gate: at least one geometrically valid, named region.</summary>
    public bool Step3Ready => HasReadyRegions;

    /// <summary>Reason the *current* step's Next action is blocked; <see cref="SetupStepGateReason.None"/>
    /// when the step is complete.</summary>
    public SetupStepGateReason CurrentStepGateReason => CurrentStepIndex switch
    {
        0 => Step1GateReason,
        1 => Step2GateReason,
        2 => Step3GateReason,
        _ => SetupStepGateReason.None,
    };

    public SetupStepGateReason Step1GateReason =>
        string.IsNullOrWhiteSpace(ProfileName) ? SetupStepGateReason.NeedsProfileName :
        !HasReadyTarget ? SetupStepGateReason.NeedsCaptureTarget :
        SetupStepGateReason.None;

    public SetupStepGateReason Step2GateReason =>
        !HasReadyLanguages ? SetupStepGateReason.NeedsLanguages :
        !HasReadyProvider ? SetupStepGateReason.NeedsProvider :
        SetupStepGateReason.None;

    public SetupStepGateReason Step3GateReason =>
        !HasReadyRegions ? SetupStepGateReason.NeedsRegions : SetupStepGateReason.None;

    /// <summary>A step is reachable (clickable in the step bar) once every step before it is
    /// complete, or once the wizard has already advanced to or past it — completed steps stay
    /// reachable for jumping back (design spec 5.2 "已完成步可回跳,未到达步禁用").</summary>
    public bool IsStep1Reachable => true;

    public bool IsStep2Reachable => CurrentStepIndex >= 1 || Step1Ready;

    public bool IsStep3Reachable => CurrentStepIndex >= 2 || (Step1Ready && Step2Ready);

    public bool IsStep4Reachable => CurrentStepIndex >= 3 || (Step1Ready && Step2Ready && Step3Ready);

    /// <summary>The reason a not-yet-reachable step is disabled: the first unmet gate among the
    /// steps before it. <see cref="SetupStepGateReason.None"/> when the step is reachable.</summary>
    public SetupStepGateReason GetStepBlockingReason(int stepIndex) => stepIndex switch
    {
        0 => SetupStepGateReason.None,
        1 => IsStep2Reachable ? SetupStepGateReason.None : Step1GateReason,
        2 => IsStep3Reachable ? SetupStepGateReason.None : (Step1Ready ? Step2GateReason : Step1GateReason),
        3 => IsStep4Reachable
            ? SetupStepGateReason.None
            : !Step1Ready ? Step1GateReason : !Step2Ready ? Step2GateReason : Step3GateReason,
        _ => SetupStepGateReason.None,
    };

    /// <summary>Loads capture targets and providers for a new profile.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadCatalogsAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Loads catalogs and pre-populates every field from an existing profile for editing.</summary>
    public async Task LoadForEditAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await LoadCatalogsAsync(cancellationToken).ConfigureAwait(true);
        ProfileEditModel? model =
            await _profileService.LoadForEditAsync(profileId, cancellationToken).ConfigureAwait(true);
        if (model is null)
        {
            return;
        }

        _editingProfileId = model.ProfileId;
        ProfileName = model.Name;
        SourceLanguage = model.SourceLanguage;
        TargetLanguage = model.TargetLanguage;
        _desktopRegionStates.Clear();
        foreach (ProfileCaptureTargetDraft captureTarget in model.EffectiveCaptureTargets)
        {
            if (captureTarget.DesktopRegion is { } region)
            {
                _desktopRegionStates[captureTarget.TargetId] = new DesktopRegionState(
                    Enabled: true,
                    region.X,
                    region.Y,
                    region.Width,
                    region.Height);
            }
        }
        CaptureProbeTarget[] selectedTargets = model.EffectiveCaptureTargets
            .Select(captureTarget =>
                Targets.FirstOrDefault(target => target.TargetId.Value == captureTarget.TargetId) ??
                Targets.FirstOrDefault(target => string.Equals(
                    target.DisplayName,
                    captureTarget.Name.Replace(" — fixed area", string.Empty),
                    StringComparison.OrdinalIgnoreCase)))
            .Where(target => target is not null)
            .Cast<CaptureProbeTarget>()
            .DistinctBy(target => target.TargetId.Value)
            .ToArray();
        SelectedTarget = null;
        SetSelectedTargets(selectedTargets.Length > 0
            ? selectedTargets
            : Targets.Take(1));
        SelectedProvider = Providers.FirstOrDefault(provider =>
            string.Equals(provider.Name, model.TranslationProviderId, StringComparison.OrdinalIgnoreCase))
            ?? Providers.FirstOrDefault();
        ReplaceRegions(model.Regions.Select(ToWorkbenchRegion));

        OnPropertyChanged(nameof(IsEditing));
        SaveCommand.NotifyCanExecuteChanged();
    }

    public void AddRegion(
        string name,
        RegionPriorityLevel priority,
        RegionContextRole contextRole = RegionContextRole.None)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        WorkbenchRegionItem region = WorkbenchRegionItem.CreateDefault(name.Trim());
        region.Priority = priority;
        region.ContextRole = contextRole;
        Regions.Add(region);
        // Always select the freshly added region: RegionCanvas's draw-to-add contract requires the
        // host to select the new region synchronously, before the canvas applies the drawn bounds
        // to SelectedRegion (see RegionCanvas.RegionAdded doc comment).
        SelectedRegion = region;
    }

    public void RemoveRegion(WorkbenchRegionItem region)
    {
        ArgumentNullException.ThrowIfNull(region);
        int index = Regions.IndexOf(region);
        if (index < 0)
        {
            return;
        }

        Regions.RemoveAt(index);
        if (ReferenceEquals(SelectedRegion, region))
        {
            SelectedRegion = Regions.Count == 0 ? null : Regions[Math.Clamp(index, 0, Regions.Count - 1)];
        }
    }

    public void SetSelectedTargets(IEnumerable<CaptureProbeTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        CaptureProbeTarget[] selected = targets
            .Where(target => Targets.Contains(target))
            .DistinctBy(target => target.TargetId.Value)
            .ToArray();
        SelectedTargets.Clear();
        foreach (CaptureProbeTarget target in selected)
        {
            SelectedTargets.Add(target);
        }

        if (SelectedTarget is null || !SelectedTargets.Contains(SelectedTarget))
        {
            SelectedTarget = SelectedTargets.FirstOrDefault();
        }
        NotifyTargetSelectionChanged();
    }

    /// <summary>Writes the entered secret straight to the credential store for the selected provider.</summary>
    public async Task SetProviderSecretAsync(string secret, CancellationToken cancellationToken = default)
    {
        if (SelectedProvider is null || string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        await _secrets.SetSecretAsync(SelectedProvider.Name, secret, cancellationToken).ConfigureAwait(true);
        await LoadCatalogsAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Step 2 "test translation" (design spec 5.2): sends one sample sentence through the
    /// real <see cref="ITranslationProbe"/> and reports latency/result or the real failure — never a
    /// fabricated success.</summary>
    private async Task TestTranslationAsync()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        const string sampleText = "Hello, adventurer.";
        IsTranslationTestBusy = true;
        TranslationTestError = string.Empty;
        TranslationTestResult = string.Empty;
        try
        {
            TranslationProbeResult result = await _translationProbe.TranslateAsync(
                new TranslationProbeRequest(sampleText, SourceLanguage, TargetLanguage, Context: null),
                CancellationToken.None).ConfigureAwait(true);
            if (result.ErrorCode is not null)
            {
                TranslationTestError = result.ErrorCode;
            }
            else
            {
                TranslationTestResult = result.Text;
                TranslationTestLatency = result.Latency;
            }
        }
        catch (Exception exception)
        {
            TranslationTestError = exception.Message;
        }
        finally
        {
            IsTranslationTestBusy = false;
        }
    }

    /// <summary>Step 3 "试跑 OCR" (design spec 5.2): runs the real <see cref="IOcrProbe"/> against a
    /// caller-supplied encoded crop. The view model never captures pixels itself (that requires
    /// WinUI imaging APIs owned by the page); it only ever forwards real bytes or reports a real
    /// probe failure — it does not fabricate a crop when none is available.</summary>
    public async Task<(string? Text, TimeSpan Latency, string? Error)> TestOcrAsync(
        WorkbenchRegionItem region,
        int pixelWidth,
        int pixelHeight,
        ReadOnlyMemory<byte> encodedCrop,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        try
        {
            OcrProbeResult result = await _ocrProbe.RecognizeAsync(
                new OcrProbeRequest(new RegionId(region.RegionId), pixelWidth, pixelHeight, encodedCrop),
                cancellationToken).ConfigureAwait(true);
            return (result.Text, result.Latency, null);
        }
        catch (Exception exception)
        {
            return (null, TimeSpan.Zero, exception.Message);
        }
    }

    private async Task LoadCatalogsAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            CaptureProbeResult probe = await _captureProbe
                .ProbeAsync(new CaptureProbeRequest(NameFilter: null), cancellationToken)
                .ConfigureAwait(true);
            Guid[] selectedTargetIds = SelectedTargets
                .Select(target => target.TargetId.Value)
                .ToArray();
            Targets.Clear();
            foreach (CaptureProbeTarget target in probe.Targets)
            {
                Targets.Add(target);
            }

            IReadOnlyList<ProviderRow> providers =
                await _settingsService.GetProvidersAsync(cancellationToken).ConfigureAwait(true);
            string? selectedProviderName = SelectedProvider?.Name;
            Providers.Clear();
            foreach (ProviderRow provider in providers.Where(provider =>
                         provider.IsTranslationProvider && provider.IsSelectable))
            {
                Providers.Add(provider);
            }

            CaptureProbeTarget[] restoredTargets = Targets
                .Where(target => selectedTargetIds.Contains(target.TargetId.Value))
                .ToArray();
            SetSelectedTargets(restoredTargets.Length > 0
                ? restoredTargets
                : Targets.Take(1));
            SelectedProvider = Providers.FirstOrDefault(provider =>
                string.Equals(provider.Name, selectedProviderName, StringComparison.OrdinalIgnoreCase))
                ?? Providers.FirstOrDefault();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task SaveAsync(bool allowDraft)
    {
        if ((!allowDraft && !CanSave) ||
            (allowDraft && !CanSaveDraft) ||
            SelectedTarget is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        SaveCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
        try
        {
            SaveActiveDesktopRegionState();
            ProfileCaptureTargetDraft[] captureTargets = SelectedTargets
                .Select(ToCaptureTargetDraft)
                .ToArray();
            ProfileCaptureTargetDraft primaryTarget = captureTargets.First();
            var draft = new ProfileEditModel(
                _editingProfileId,
                ProfileName.Trim(),
                SourceLanguage,
                TargetLanguage,
                primaryTarget.TargetId,
                primaryTarget.Name,
                primaryTarget.Kind,
                primaryTarget.Resolution,
                SelectedProvider?.Name ?? string.Empty,
                Regions.Select(ToProfileRegionDraft).ToArray(),
                primaryTarget.DesktopRegion,
                captureTargets);
            SavedProfileId = await _profileService.SaveAsync(draft).ConfigureAwait(true);
            _editingProfileId = SavedProfileId;
            WasSavedAsDraft = !IsConfigurationReady;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            SaveCommand.NotifyCanExecuteChanged();
            SaveDraftCommand.NotifyCanExecuteChanged();
        }
    }

    private void GoBack()
    {
        if (CanGoBack)
        {
            CurrentStepIndex--;
        }
    }

    private void GoNext()
    {
        if (CanGoNext)
        {
            CurrentStepIndex++;
        }
    }

    /// <summary>Jumps directly to <paramref name="stepIndex"/> when the step bar exposes it as
    /// reachable (already visited, or every prior gate is satisfied).</summary>
    public void GoToStep(int stepIndex)
    {
        if (stepIndex < 0 || stepIndex >= StepCount)
        {
            return;
        }

        bool reachable = stepIndex switch
        {
            0 => IsStep1Reachable,
            1 => IsStep2Reachable,
            2 => IsStep3Reachable,
            3 => IsStep4Reachable,
            _ => false,
        };
        if (reachable)
        {
            CurrentStepIndex = stepIndex;
        }
    }

    partial void OnCurrentStepIndexChanged(int value)
    {
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    partial void OnProfileNameChanged(string value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
        NotifyStepGatesChanged();
    }

    partial void OnSelectedTargetChanging(
        CaptureProbeTarget? oldValue,
        CaptureProbeTarget? newValue)
    {
        if (!_isSynchronizingDesktopRegion && oldValue is not null)
        {
            SaveActiveDesktopRegionState(oldValue);
        }
    }

    partial void OnSelectedTargetChanged(CaptureProbeTarget? value)
    {
        _isSynchronizingDesktopRegion = true;
        try
        {
            if (value is not null &&
                _desktopRegionStates.TryGetValue(value.TargetId.Value, out DesktopRegionState state))
            {
                UseDesktopFixedRegion = state.Enabled;
                DesktopRegionX = state.X;
                DesktopRegionY = state.Y;
                DesktopRegionWidth = state.Width;
                DesktopRegionHeight = state.Height;
            }
            else if (value is not null && IsDisplay(value))
            {
                UseDesktopFixedRegion = false;
                DesktopRegionX = value.DesktopX;
                DesktopRegionY = value.DesktopY;
                DesktopRegionWidth = value.PixelWidth;
                DesktopRegionHeight = value.PixelHeight;
            }
            else
            {
                UseDesktopFixedRegion = false;
            }
        }
        finally
        {
            _isSynchronizingDesktopRegion = false;
        }
        OnPropertyChanged(nameof(CanUseDesktopFixedRegion));
        NotifyTargetSelectionChanged();
    }

    partial void OnUseDesktopFixedRegionChanged(bool value)
    {
        if (!_isSynchronizingDesktopRegion)
        {
            SaveActiveDesktopRegionState();
        }
        NotifyTargetSelectionChanged();
    }

    partial void OnDesktopRegionXChanged(double value) => NotifyTargetBoundsChanged();

    partial void OnDesktopRegionYChanged(double value) => NotifyTargetBoundsChanged();

    partial void OnDesktopRegionWidthChanged(double value) => NotifyTargetBoundsChanged();

    partial void OnDesktopRegionHeightChanged(double value) => NotifyTargetBoundsChanged();

    partial void OnSelectedProviderChanged(ProviderRow? value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
        TestTranslationCommand.NotifyCanExecuteChanged();
        NotifyStepGatesChanged();
    }

    partial void OnSourceLanguageChanged(string value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
        TestTranslationCommand.NotifyCanExecuteChanged();
        NotifyStepGatesChanged();
    }

    partial void OnTargetLanguageChanged(string value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
        TestTranslationCommand.NotifyCanExecuteChanged();
        NotifyStepGatesChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTranslationTestBusyChanged(bool value) => TestTranslationCommand.NotifyCanExecuteChanged();

    private void AddDefaultRegion() => AddRegion("Dialogue", RegionPriorityLevel.P0);

    private void ReplaceRegions(IEnumerable<WorkbenchRegionItem> regions)
    {
        // ObservableCollection.Clear() raises Reset with no OldItems, so unsubscribe explicitly
        // first rather than relying on OnRegionsCollectionChanged to catch it.
        foreach (WorkbenchRegionItem existing in Regions)
        {
            existing.PropertyChanged -= OnRegionPropertyChanged;
        }
        Regions.Clear();
        foreach (WorkbenchRegionItem region in regions)
        {
            Regions.Add(region);
        }
        if (Regions.Count == 0)
        {
            AddDefaultRegion();
        }
        SelectedRegion = Regions.FirstOrDefault();
    }

    private void OnRegionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (WorkbenchRegionItem region in e.OldItems.OfType<WorkbenchRegionItem>())
            {
                region.PropertyChanged -= OnRegionPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (WorkbenchRegionItem region in e.NewItems.OfType<WorkbenchRegionItem>())
            {
                region.PropertyChanged += OnRegionPropertyChanged;
            }
        }
        NotifyReadinessChanged();
    }

    private void OnRegionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkbenchRegionItem.Name) or nameof(WorkbenchRegionItem.X) or
            nameof(WorkbenchRegionItem.Y) or nameof(WorkbenchRegionItem.Width) or
            nameof(WorkbenchRegionItem.Height))
        {
            NotifyReadinessChanged();
        }
    }

    private static ProfileRegionDraft ToProfileRegionDraft(WorkbenchRegionItem region) => new(
        region.Name,
        region.Priority,
        region.RegionId,
        region.X,
        region.Y,
        region.Width,
        region.Height,
        region.ContextRole);

    /// <summary>Preserves <see cref="ProfileRegionDraft.RegionId"/> across the round trip (unlike
    /// <see cref="WorkbenchRegionItem.CreateDefault"/>, which always mints a new id) so
    /// <c>RealProfileService.Apply</c> can match this region back to its saved counterpart by id and
    /// keep richer settings (translation channels, OCR tuning) that the wizard's lighter draft shape
    /// does not round-trip.</summary>
    private static WorkbenchRegionItem ToWorkbenchRegion(ProfileRegionDraft draft)
    {
        WorkbenchRegionDraft baseDraft = WorkbenchRegionItem.CreateDefault(draft.Name).ToDraft() with
        {
            RegionId = draft.RegionId,
            X = draft.X,
            Y = draft.Y,
            Width = draft.Width,
            Height = draft.Height,
            Priority = draft.Priority,
            ContextRole = draft.ContextRole,
        };
        return new WorkbenchRegionItem(baseDraft);
    }

    private bool HasValidTargetState(CaptureProbeTarget target)
    {
        if (!_desktopRegionStates.TryGetValue(target.TargetId.Value, out DesktopRegionState state) ||
            !state.Enabled)
        {
            return true;
        }
        return IsDisplay(target) && IsValidDesktopRegion(state);
    }

    private ProfileCaptureTargetDraft ToCaptureTargetDraft(CaptureProbeTarget target)
    {
        DesktopRegionState state = target == SelectedTarget
            ? CurrentDesktopRegionState
            : _desktopRegionStates.GetValueOrDefault(target.TargetId.Value);
        OverlayPixelRect? region = state.Enabled && IsValidDesktopRegion(state)
            ? new OverlayPixelRect((int)state.X, (int)state.Y, (int)state.Width, (int)state.Height)
            : null;
        return new ProfileCaptureTargetDraft(
            target.TargetId.Value,
            target.DisplayName,
            region is null ? NormalizeTargetKind(target.Kind) : "DesktopFixedRegion",
            $"{target.PixelWidth}×{target.PixelHeight} · {target.Dpi}dpi",
            region);
    }

    private void SaveActiveDesktopRegionState(CaptureProbeTarget? target = null)
    {
        CaptureProbeTarget? activeTarget = target ?? SelectedTarget;
        if (activeTarget is null)
        {
            return;
        }
        _desktopRegionStates[activeTarget.TargetId.Value] = CurrentDesktopRegionState;
    }

    private DesktopRegionState CurrentDesktopRegionState => new(
        UseDesktopFixedRegion,
        DesktopRegionX,
        DesktopRegionY,
        DesktopRegionWidth,
        DesktopRegionHeight);

    private static bool IsValidDesktopRegion(DesktopRegionState state) =>
        IsInteger(state.X) &&
        IsInteger(state.Y) &&
        IsInteger(state.Width) &&
        IsInteger(state.Height) &&
        state.X is >= int.MinValue and <= int.MaxValue &&
        state.Y is >= int.MinValue and <= int.MaxValue &&
        state.Width is > 0 and <= 16_384 &&
        state.Height is > 0 and <= 16_384 &&
        state.X + state.Width <= int.MaxValue &&
        state.Y + state.Height <= int.MaxValue;

    private static bool IsInteger(double value) =>
        double.IsFinite(value) && value == Math.Truncate(value);

    private static string NormalizeTargetKind(string kind) =>
        string.Equals(kind, "Monitor", StringComparison.OrdinalIgnoreCase)
            ? "Display"
            : kind;

    private static bool IsDisplay(CaptureProbeTarget target) =>
        string.Equals(target.Kind, "Monitor", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(target.Kind, "Display", StringComparison.OrdinalIgnoreCase);

    private void NotifyTargetSelectionChanged()
    {
        OnPropertyChanged(nameof(HasReadyTarget));
        OnPropertyChanged(nameof(IsConfigurationReady));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanSaveDraft));
        OnPropertyChanged(nameof(Step1Ready));
        NotifyStepGatesChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
    }

    private void NotifyTargetBoundsChanged()
    {
        if (!_isSynchronizingDesktopRegion)
        {
            SaveActiveDesktopRegionState();
        }
        NotifyTargetSelectionChanged();
    }

    private void NotifyReadinessChanged()
    {
        OnPropertyChanged(nameof(HasReadyRegions));
        OnPropertyChanged(nameof(IsConfigurationReady));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanSaveDraft));
        OnPropertyChanged(nameof(Step3Ready));
        NotifyStepGatesChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
    }

    private void NotifyStepGatesChanged()
    {
        OnPropertyChanged(nameof(CurrentStepGateReason));
        OnPropertyChanged(nameof(Step1GateReason));
        OnPropertyChanged(nameof(Step2GateReason));
        OnPropertyChanged(nameof(Step3GateReason));
        OnPropertyChanged(nameof(IsStep2Reachable));
        OnPropertyChanged(nameof(IsStep3Reachable));
        OnPropertyChanged(nameof(IsStep4Reachable));
        OnPropertyChanged(nameof(CanGoNext));
        NextCommand.NotifyCanExecuteChanged();
    }

    private readonly record struct DesktopRegionState(
        bool Enabled,
        double X,
        double Y,
        double Width,
        double Height);
}
