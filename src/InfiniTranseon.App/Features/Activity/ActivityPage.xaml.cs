using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using InfiniTranseon.App.Controls;
using InfiniTranseon.App.Features.History;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace InfiniTranseon.App.Features.Activity;

public sealed partial class ActivityPage : Page
{
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");
    private readonly RuntimeEventHub _runtimeEvents;
    private readonly List<ActivityEventRow> _allEvents = [];
    private bool _subscribed;
    private bool _historyLoaded;

    public ActivityPage()
    {
        ViewModel = App.GetService<DiagnosticsViewModel>();
        _runtimeEvents = App.GetService<RuntimeEventHub>();
        InitializeComponent();
        // Selected here rather than with SelectedIndex="0" in the markup: the ComboBox raises
        // SelectionChanged while it is still being parsed, and ApplyFilter then reaches for the
        // empty-state and repeater declared further down the file, which do not exist yet.
        ActivityKindFilter.SelectedIndex = 0;
    }

    public DiagnosticsViewModel ViewModel { get; }
    public ObservableCollection<ActivityEventRow> Events { get; } = [];
    public string ActivityTitle => Strings.GetString("ActivityTitle");
    public string ActivitySubtitle => Strings.GetString("ActivitySubtitle");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        await ViewModel.InitializeAsync();
        _allEvents.Clear();
        _allEvents.AddRange(ViewModel.Events.Select(FromDiagnostic));
        _allEvents.AddRange(_runtimeEvents.Snapshot().Select(FromRuntime));
        ApplyFilter();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Subscribe()
    {
        if (_subscribed) return;
        _runtimeEvents.OcrRecognized += OnOcrRecognized;
        _runtimeEvents.TranslationReceived += OnTranslationReceived;
        _runtimeEvents.DiagnosticRaised += OnDiagnosticRaised;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _runtimeEvents.OcrRecognized -= OnOcrRecognized;
        _runtimeEvents.TranslationReceived -= OnTranslationReceived;
        _runtimeEvents.DiagnosticRaised -= OnDiagnosticRaised;
        _subscribed = false;
    }

    private void OnOcrRecognized(object? sender, LiveOcrRecognized value) =>
        Enqueue(FromOcr(value));

    private void OnTranslationReceived(object? sender, LiveTranslationReceived value) =>
        Enqueue(FromTranslation(value));

    private void OnDiagnosticRaised(object? sender, RuntimeDiagnosticRaised value) =>
        Enqueue(FromRuntimeDiagnostic(value));

    private void Enqueue(ActivityEventRow row)
    {
        var dispatcher = DispatcherQueue;
        _ = dispatcher.TryEnqueue(() =>
        {
            _allEvents.Add(row);
            if (_allEvents.Count > RuntimeEventHub.RingBufferCapacity)
                _allEvents.RemoveAt(0);
            ApplyFilter();
        });
    }

    private void OnFilterChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
            ApplyFilter();
    }

    private void OnKindFilterChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyFilter();

    private void ApplyFilter()
    {
        string query = ActivitySearchBox.Text.Trim();
        string kind = (ActivityKindFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        IEnumerable<ActivityEventRow> filtered = _allEvents
            .Where(item =>
                (kind == "all" || string.Equals(item.Kind, kind, StringComparison.Ordinal)) &&
                (query.Length == 0 ||
                    item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    item.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    item.Scope.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(item => item.OccurredAtUtc);
        Events.Clear();
        foreach (ActivityEventRow item in filtered)
            Events.Add(item);
        ActivityEmptyState.Visibility = Events.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ActivityRepeater.Visibility = Events.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnActivitySectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        string tag = sender.SelectedItem?.Tag?.ToString() ?? "live";
        LivePanel.Visibility = tag == "live" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        ReportsPanel.Visibility = tag == "reports" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "history" && !_historyLoaded)
        {
            _historyLoaded = true;
            HistoryPanel.Children.Add(new Frame
            {
                Content = new HistoryPage(),
            });
        }
    }

    private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = "InfiniTranseon-activity" };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            await using Stream stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            await JsonSerializer.SerializeAsync(stream, _allEvents);
            await stream.FlushAsync();
        }
        catch (Exception exception)
        {
            await ShowActionErrorAsync(exception.Message);
        }
    }

    private async void OnOpenCrashReportsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = App.GetService<AppDataOptions>().CrashReportDirectory;
            Directory.CreateDirectory(directory);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(directory);
            if (!await Launcher.LaunchFolderAsync(folder))
                throw new InvalidOperationException(
                    Strings.GetString("DiagnosticsOpenFolderFailed"));
        }
        catch (Exception exception)
        {
            await ShowActionErrorAsync(exception.Message);
        }
    }

    private async Task ShowActionErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = Strings.GetString("DiagnosticsActionErrorTitle"),
            Content = message,
            CloseButtonText = Strings.GetString("DiagnosticsActionErrorClose"),
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private static ActivityEventRow FromRuntime(RuntimeHubEvent value) => value.Payload switch
    {
        LiveOcrRecognized ocr => FromOcr(ocr),
        LiveTranslationReceived translation => FromTranslation(translation),
        RuntimeDiagnosticRaised diagnostic => FromRuntimeDiagnostic(diagnostic),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ActivityEventRow FromOcr(LiveOcrRecognized value) => new(
        value.OccurredAtUtc,
        "ocr",
        Strings.GetString("ActivityKindOcrLabel"),
        value.Area.ToString(),
        Strings.GetString("ActivityOcrTitle"),
        string.Format(
            CultureInfo.CurrentCulture,
            Strings.GetString("ActivityOcrDetail"),
            value.Lines.Count,
            value.ModelId),
        value.TerminalErrorCode is null ? StatusSeverity.Success : StatusSeverity.Critical);

    private static ActivityEventRow FromTranslation(LiveTranslationReceived value) => new(
        value.OccurredAtUtc,
        "translation",
        Strings.GetString("ActivityKindTranslationLabel"),
        value.Area.ToString(),
        value.ProviderId,
        string.Format(
            CultureInfo.CurrentCulture,
            Strings.GetString("ActivityTranslationDetail"),
            value.Stage,
            value.Latency.TotalMilliseconds,
            value.CacheHit ? Strings.GetString("ActivityCacheHit") : Strings.GetString("ActivityCacheMiss")),
        value.TerminalErrorCode is null ? StatusSeverity.Success : StatusSeverity.Critical);

    private static ActivityEventRow FromRuntimeDiagnostic(RuntimeDiagnosticRaised value) => new(
        value.OccurredAtUtc,
        "diagnostic",
        Strings.GetString("ActivityKindDiagnosticLabel"),
        value.ErrorCode,
        value.MessageKey,
        value.ErrorCode,
        value.Severity switch
        {
            RuntimeDiagnosticSeverity.Error or RuntimeDiagnosticSeverity.Critical => StatusSeverity.Critical,
            RuntimeDiagnosticSeverity.Warning => StatusSeverity.Warning,
            _ => StatusSeverity.Info,
        });

    private static ActivityEventRow FromDiagnostic(DiagnosticEvent value) => new(
        DateTimeOffset.TryParse(value.Timestamp, out DateTimeOffset timestamp)
            ? timestamp
            : DateTimeOffset.MinValue,
        "diagnostic",
        Strings.GetString("ActivityKindDiagnosticLabel"),
        value.Scope,
        value.Title,
        $"{value.CurrentBehavior} {value.RecoveryAction}".Trim(),
        value.Severity);
}

public sealed class ActivityEventRow(
    DateTimeOffset occurredAtUtc,
    string kind,
    string kindLabel,
    string scope,
    string title,
    string detail,
    StatusSeverity severity)
{
    public DateTimeOffset OccurredAtUtc { get; set; } = occurredAtUtc;
    public string Kind { get; set; } = kind;
    public string KindLabel { get; set; } = kindLabel;
    public string Scope { get; set; } = scope;
    public string Title { get; set; } = title;
    public string Detail { get; set; } = detail;
    public StatusSeverity Severity { get; set; } = severity;

    public string Time => OccurredAtUtc == DateTimeOffset.MinValue
        ? "—"
        : OccurredAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
