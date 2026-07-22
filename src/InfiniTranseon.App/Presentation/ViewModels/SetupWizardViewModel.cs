using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace InfiniTranseon.App.Presentation.ViewModels;

/// <summary>
/// Drives the four-step guided setup: Capture targets → Languages &amp; services → Regions →
/// Test &amp; save. Pure step-navigation logic only; step content and localized captions live in the
/// page so this stays free of WinUI and unit-testable.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    public const int StepCount = 4;

    public SetupWizardViewModel()
    {
        BackCommand = new RelayCommand(GoBack, () => CanGoBack);
        NextCommand = new RelayCommand(GoNext, () => CanGoNext);
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

    public IRelayCommand BackCommand { get; }

    public IRelayCommand NextCommand { get; }

    public int CurrentStepNumber => CurrentStepIndex + 1;

    public bool IsStep1 => CurrentStepIndex == 0;

    public bool IsStep2 => CurrentStepIndex == 1;

    public bool IsStep3 => CurrentStepIndex == 2;

    public bool IsStep4 => CurrentStepIndex == 3;

    public bool CanGoBack => CurrentStepIndex > 0;

    public bool CanGoNext => CurrentStepIndex < StepCount - 1;

    public bool IsLastStep => CurrentStepIndex == StepCount - 1;

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
}
