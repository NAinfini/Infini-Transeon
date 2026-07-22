using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfiniTranseon.Contracts.Probes;

namespace InfiniTranseon.App.Presentation.ViewModels;

/// <summary>
/// Drives the four-step guided setup: Capture target → Languages &amp; translator → Regions →
/// Review &amp; save. Step 1 targets come from the capture probe, step 2 providers from the settings
/// service (with secret entry writing straight through the secret service), step 3 is an in-memory
/// region editor, and step 4 builds a presentation <see cref="ProfileEditModel"/> and persists it via
/// the profile service. Stays free of WinUI and Core types so it remains unit-testable.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    public const int StepCount = 4;

    private readonly ICaptureProbe _captureProbe;
    private readonly ISettingsService _settingsService;
    private readonly ISecretReferenceService _secrets;
    private readonly IProfileService _profileService;
    private Guid _editingProfileId;

    public SetupWizardViewModel(
        ICaptureProbe captureProbe,
        ISettingsService settingsService,
        ISecretReferenceService secrets,
        IProfileService profileService)
    {
        ArgumentNullException.ThrowIfNull(captureProbe);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(profileService);
        _captureProbe = captureProbe;
        _settingsService = settingsService;
        _secrets = secrets;
        _profileService = profileService;
        BackCommand = new RelayCommand(GoBack, () => CanGoBack);
        NextCommand = new RelayCommand(GoNext, () => CanGoNext);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
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
    public partial int CurrentStepIndex { get; set; }

    [ObservableProperty]
    public partial CaptureProbeTarget? SelectedTarget { get; set; }

    [ObservableProperty]
    public partial ProviderRow? SelectedProvider { get; set; }

    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceLanguage { get; set; } = "ja";

    [ObservableProperty]
    public partial string TargetLanguage { get; set; } = "zh-Hans";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ObservableProperty]
    public partial Guid SavedProfileId { get; set; }

    public ObservableCollection<CaptureProbeTarget> Targets { get; } = [];

    public ObservableCollection<ProviderRow> Providers { get; } = [];

    public ObservableCollection<ProfileRegionDraft> Regions { get; } =
        [new ProfileRegionDraft("Dialogue", RegionPriorityLevel.P0)];

    public IRelayCommand BackCommand { get; }

    public IRelayCommand NextCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public int CurrentStepNumber => CurrentStepIndex + 1;

    public bool IsStep1 => CurrentStepIndex == 0;

    public bool IsStep2 => CurrentStepIndex == 1;

    public bool IsStep3 => CurrentStepIndex == 2;

    public bool IsStep4 => CurrentStepIndex == 3;

    public bool CanGoBack => CurrentStepIndex > 0;

    public bool CanGoNext => CurrentStepIndex < StepCount - 1;

    public bool IsLastStep => CurrentStepIndex == StepCount - 1;

    public bool IsEditing => _editingProfileId != Guid.Empty;

    public bool CanSave =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(ProfileName) &&
        SelectedTarget is not null &&
        Regions.Count > 0;

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
        SelectedTarget = Targets.FirstOrDefault(target => target.TargetId.Value == model.TargetId)
            ?? Targets.FirstOrDefault();
        SelectedProvider = Providers.FirstOrDefault(provider =>
            string.Equals(provider.Name, model.TranslationProviderId, StringComparison.OrdinalIgnoreCase))
            ?? Providers.FirstOrDefault();
        Regions.Clear();
        foreach (ProfileRegionDraft region in model.Regions)
        {
            Regions.Add(region);
        }

        OnPropertyChanged(nameof(IsEditing));
        SaveCommand.NotifyCanExecuteChanged();
    }

    public void AddRegion(string name, RegionPriorityLevel priority)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Regions.Add(new ProfileRegionDraft(name.Trim(), priority));
        SaveCommand.NotifyCanExecuteChanged();
    }

    public void RemoveRegion(ProfileRegionDraft region)
    {
        Regions.Remove(region);
        SaveCommand.NotifyCanExecuteChanged();
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

    private async Task LoadCatalogsAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            CaptureProbeResult probe = await _captureProbe
                .ProbeAsync(new CaptureProbeRequest(NameFilter: null), cancellationToken)
                .ConfigureAwait(true);
            Targets.Clear();
            foreach (CaptureProbeTarget target in probe.Targets)
            {
                Targets.Add(target);
            }

            IReadOnlyList<ProviderRow> providers =
                await _settingsService.GetProvidersAsync(cancellationToken).ConfigureAwait(true);
            Providers.Clear();
            foreach (ProviderRow provider in providers)
            {
                Providers.Add(provider);
            }

            SelectedTarget ??= Targets.FirstOrDefault();
            SelectedProvider ??= Providers.FirstOrDefault();
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

    private async Task SaveAsync()
    {
        if (!CanSave || SelectedTarget is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        SaveCommand.NotifyCanExecuteChanged();
        try
        {
            var draft = new ProfileEditModel(
                _editingProfileId,
                ProfileName.Trim(),
                SourceLanguage,
                TargetLanguage,
                SelectedTarget.TargetId.Value,
                SelectedTarget.DisplayName,
                SelectedTarget.Kind,
                $"{SelectedTarget.PixelWidth}×{SelectedTarget.PixelHeight} · {SelectedTarget.Dpi}dpi",
                SelectedProvider?.Name ?? string.Empty,
                Regions.ToArray());
            SavedProfileId = await _profileService.SaveAsync(draft).ConfigureAwait(true);
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

    partial void OnCurrentStepIndexChanged(int value)
    {
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    partial void OnProfileNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnSelectedTargetChanged(CaptureProbeTarget? value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();
}
