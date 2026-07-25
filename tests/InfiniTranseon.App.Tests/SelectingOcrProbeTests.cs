using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Contracts.Probes;

namespace InfiniTranseon.App.Tests;

public sealed class SelectingOcrProbeTests
{
    /// <summary>
    /// Only the Windows recognizer can pick a language for itself, from the account's display
    /// languages. Routing "auto" to the per-language local models would always fail.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void AutomaticLanguageAlwaysUsesTheWindowsRecognizer(string? languageTag)
    {
        (SelectingOcrProbe probe, IOcrProbe windows, _) = Create(AppOcrBackend.Local, []);
        Assert.Same(windows, probe.Select(languageTag));
    }

    [Fact]
    public void WindowsIsPreferredForALanguageThisMachineCanAlreadyRead()
    {
        (SelectingOcrProbe probe, IOcrProbe windows, _) = Create(AppOcrBackend.Automatic, ["ja-JP"]);
        Assert.Same(windows, probe.Select("ja"));
    }

    /// <summary>
    /// The case the local models exist for: Windows OCR language packs are Features on Demand that
    /// an unpackaged application cannot install, so without this the language is simply unreadable.
    /// </summary>
    [Fact]
    public void LocalModelsCoverALanguageWindowsHasNoRecognizerFor()
    {
        (SelectingOcrProbe probe, _, IOcrProbe local) = Create(AppOcrBackend.Automatic, ["en-US"]);
        Assert.Same(local, probe.Select("ja"));
    }

    [Fact]
    public void AnExplicitChoiceOverridesWhatIsInstalled()
    {
        (SelectingOcrProbe windowsOnly, IOcrProbe windows, _) =
            Create(AppOcrBackend.Windows, []);
        Assert.Same(windows, windowsOnly.Select("ja"));

        (SelectingOcrProbe localOnly, _, IOcrProbe local) =
            Create(AppOcrBackend.Local, ["ja-JP"]);
        Assert.Same(local, localOnly.Select("ja"));
    }

    /// <summary>
    /// Both sides change while the app is open — the user can add a Windows language pack, and the
    /// packages install in the background — so the choice cannot be made once and cached.
    /// </summary>
    [Fact]
    public void TheChoiceIsMadeAgainForEveryRecognition()
    {
        var windowsTags = new List<string>();
        var backend = AppOcrBackend.Automatic;
        var windows = new StubProbe();
        var local = new StubProbe();
        var probe = new SelectingOcrProbe(
            windows,
            local,
            new WindowsOcrLanguageAvailability(() => windowsTags),
            () => backend);

        Assert.Same(local, probe.Select("ja"));
        windowsTags.Add("ja-JP");
        Assert.Same(windows, probe.Select("ja"));
        backend = AppOcrBackend.Local;
        Assert.Same(local, probe.Select("ja"));
    }

    private static (SelectingOcrProbe Probe, IOcrProbe Windows, IOcrProbe Local) Create(
        AppOcrBackend backend,
        IReadOnlyList<string> windowsRecognizerTags)
    {
        var windows = new StubProbe();
        var local = new StubProbe();
        return (
            new SelectingOcrProbe(
                windows,
                local,
                new WindowsOcrLanguageAvailability(() => windowsRecognizerTags),
                () => backend),
            windows,
            local);
    }

    private sealed class StubProbe : IOcrProbe
    {
        public ValueTask<OcrProbeResult> RecognizeAsync(
            OcrProbeRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Routing is what is under test; no crop is recognised.");
    }
}
