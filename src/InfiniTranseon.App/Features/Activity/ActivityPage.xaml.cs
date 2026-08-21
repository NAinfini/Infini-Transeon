using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    // Resolved per lookup so a UI language change takes effect without restarting; see AppStrings.
    private static ResourceLoader Strings => Localization.AppStrings.Loader;
    private readonly RuntimeEventHub _runtimeEvents;
    private readonly List<ActivityEventRow> _allEvents = [];
    private bool _subscribed;
    private bool _historyLoaded;

    public ActivityPage()
    {
        ViewModel = App.GetService<DiagnosticsViewModel>();
        _runtimeEvents = App.GetService<RuntimeEventHub>();
        InitializeComponent();
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

    private void OnKindFilterChanged(object? sender, EventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string query = ActivitySearchBox.Text.Trim();
        string kind = ActivityKindFilter.SelectedItem as string ?? "all";
        IEnumerable<ActivityEventRow> filtered = _allEvents
            .Where(item =>
                (kind == "all" || string.Equals(item.Kind, kind, StringComparison.Ordinal)) &&
                (query.Length == 0 ||
                    item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    item.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    item.Scope.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(item => item.OccurredAtUtc);
        Events.Clear();
        foreach (ActivityEventRow item in Collapse(filtered))
            Events.Add(item);
        ActivityEmptyState.Visibility = Events.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ActivityRepeater.Visibility = Events.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// Collapses a run of adjacent identical events into the newest one, tagged with how many times
    /// it occurred. A screen that holds still is re-recognized several times a second, and the feed
    /// answered "what happened" by repeating one sentence five times. The run is collapsed for
    /// display only — the stored events, and therefore the exported report, keep every occurrence —
    /// and any genuinely different event ends the run.
    /// </summary>
    private static IEnumerable<ActivityEventRow> Collapse(IEnumerable<ActivityEventRow> rows)
    {
        ActivityEventRow? pending = null;
        foreach (ActivityEventRow row in rows)
        {
            if (pending is not null && IsSameEvent(pending, row))
            {
                pending.Count++;
                continue;
            }
            if (pending is not null) yield return WithRepeatText(pending);
            pending = new ActivityEventRow(
                row.OccurredAtUtc,
                row.Kind,
                row.KindLabel,
                row.Scope,
                row.Title,
                row.Detail,
                row.Severity);
        }
        if (pending is not null) yield return WithRepeatText(pending);
    }

    private static bool IsSameEvent(ActivityEventRow left, ActivityEventRow right) =>
        left.Severity == right.Severity &&
        string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) &&
        string.Equals(left.Scope, right.Scope, StringComparison.Ordinal) &&
        string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
        string.Equals(left.Detail, right.Detail, StringComparison.Ordinal);

    private static ActivityEventRow WithRepeatText(ActivityEventRow row)
    {
        if (row.Count > 1)
        {
            row.RepeatText = string.Format(
                CultureInfo.CurrentCulture,
                Strings.GetString("ActivityRepeatCount"),
                row.Count);
        }
        return row;
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
        ScopeText(value.Area),
        Strings.GetString("ActivityOcrTitle"),
        value.TerminalErrorCode is null
            ? string.Format(
                CultureInfo.CurrentCulture,
                Strings.GetString("ActivityOcrDetail"),
                value.Lines.Count,
                value.ModelId)
            : ProbeErrorPresenter.Describe(value.TerminalErrorCode, Strings.GetString),
        value.TerminalErrorCode is null ? StatusSeverity.Success : StatusSeverity.Critical);

    private static ActivityEventRow FromTranslation(LiveTranslationReceived value) => new(
        value.OccurredAtUtc,
        "translation",
        Strings.GetString("ActivityKindTranslationLabel"),
        ScopeText(value.Area),
        ProviderText(value.ProviderId),
        string.Format(
            CultureInfo.CurrentCulture,
            Strings.GetString(value.TerminalErrorCode is null
                ? "ActivityTranslationDetail"
                : "ActivityTranslationFailureDetail"),
            StageText(value.Stage),
            LatencyText(value.Latency),
            value.TerminalErrorCode is null
                ? Strings.GetString(value.CacheHit ? "ActivityCacheHit" : "ActivityCacheMiss")
                : ProbeErrorPresenter.Describe(value.TerminalErrorCode, Strings.GetString)),
        value.TerminalErrorCode is null ? StatusSeverity.Success : StatusSeverity.Critical);

    // The capture area is an identity, not a caption: its ToString() spells out a record with a
    // region GUID inside, which told the user nothing and swamped the column it sits in.
    private static string ScopeText(CaptureAreaKey area) => Strings.GetString(area.Kind switch
    {
        CaptureAreaKind.UserRegion => "ActivityScopeUserRegion",
        CaptureAreaKind.RemainingArea => "ActivityScopeRemainingArea",
        _ => "ActivityScopeFullTarget",
    });

    // Built-in providers have a catalog name; a local model is named by the package the user
    // installed. Anything else is a REST adapter the user authored and named themselves, so its id
    // is already the most meaningful thing this row can show.
    private static string ProviderText(string providerId)
    {
        if (ProviderCatalog.Find(providerId) is { } provider)
            return provider.DisplayName;
        return providerId.StartsWith(
            EngineRuntimeComposition.LocalTranslationProviderIdPrefix,
            StringComparison.Ordinal)
            ? string.Format(
                CultureInfo.CurrentCulture,
                Strings.GetString("ActivityLocalProviderName"),
                providerId[EngineRuntimeComposition.LocalTranslationProviderIdPrefix.Length..])
            : providerId;
    }

    private static string StageText(TranslationStage stage) => Strings.GetString(stage switch
    {
        TranslationStage.Fallback => "ActivityStageFallback",
        TranslationStage.Refinement => "ActivityStageRefinement",
        _ => "ActivityStageInitial",
    });

    private static string LatencyText(TimeSpan latency) => latency < TimeSpan.FromSeconds(1)
        ? string.Format(
            CultureInfo.CurrentCulture,
            Strings.GetString("ActivityLatencyMilliseconds"),
            latency.TotalMilliseconds)
        : string.Format(
            CultureInfo.CurrentCulture,
            Strings.GetString("ActivityLatencySeconds"),
            latency.TotalSeconds);

    // The error code is what the engine states; the sentence beside it is authored here, because the
    // engine's own detail text is English prose built from an exception message. The code stays in
    // the detail column so a diagnostic raised by a newer engine is still identifiable.
    private static ActivityEventRow FromRuntimeDiagnostic(RuntimeDiagnosticRaised value) => new(
        value.OccurredAtUtc,
        "diagnostic",
        Strings.GetString("ActivityKindDiagnosticLabel"),
        Strings.GetString(RuntimeDiagnosticPresenter.CategoryResourceKeyFor(value.ErrorCode)),
        Strings.GetString(RuntimeDiagnosticPresenter.ResourceKeyFor(value.ErrorCode)),
        value.ErrorCode,
        value.Severity switch
        {
            RuntimeDiagnosticSeverity.Error or RuntimeDiagnosticSeverity.Critical => StatusSeverity.Critical,
            RuntimeDiagnosticSeverity.Warning => StatusSeverity.Warning,
            _ => StatusSeverity.Info,
        });

    // The error code is the detail rather than the title: one message key covers a whole family of
    // codes, and the code is the only part that says which member of the family this row is.
    private static ActivityEventRow FromDiagnostic(DiagnosticEvent value) => new(
        value.OccurredAtUtc,
        "diagnostic",
        Strings.GetString("ActivityKindDiagnosticLabel"),
        Strings.GetString(StatusEventPresenter.CategoryResourceKeyFor(value.Category)),
        Strings.GetString(StatusEventPresenter.MessageResourceKeyFor(value.MessageKey)),
        value.ErrorCode,
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

    /// <summary>How many adjacent identical events this display row stands for; see
    /// <c>ActivityPage.Collapse</c>. Display state, so it stays out of the exported report.</summary>
    [JsonIgnore]
    public int Count { get; set; } = 1;

    [JsonIgnore]
    public string RepeatText { get; set; } = string.Empty;

    [JsonIgnore]
    public Visibility RepeatVisibility =>
        RepeatText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string Time => OccurredAtUtc == DateTimeOffset.MinValue
        ? "—"
        : OccurredAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
