using InfiniTranseon.Contracts.Probes;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Routes each recognition to the Windows recognizer or to the downloaded PP-OCR models.
///
/// Windows is the default because it is already installed, needs no disk, and is faster. It can only
/// read the languages whose recognizer pack this machine holds, though, and those packs are Features
/// on Demand that an unpackaged application cannot install — so a Japanese game on an English Windows
/// has no Windows path at all. That is the case the local models exist for, and
/// <see cref="AppOcrBackend.Automatic"/> switches to them exactly there and nowhere else.
///
/// The routing decision is made per call rather than cached, because both sides change underneath
/// it: the user can add a Windows language pack, and the local packages install in the background.
/// </summary>
public sealed class SelectingOcrProbe(
    IOcrProbe windows,
    IOcrProbe local,
    IOcrLanguageAvailability availability,
    Func<AppOcrBackend> backend) : IOcrProbe
{
    private readonly IOcrProbe _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    private readonly IOcrProbe _local = local ?? throw new ArgumentNullException(nameof(local));

    private readonly IOcrLanguageAvailability _availability =
        availability ?? throw new ArgumentNullException(nameof(availability));

    private readonly Func<AppOcrBackend> _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public ValueTask<OcrProbeResult> RecognizeAsync(
        OcrProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Select(request.LanguageTag).RecognizeAsync(request, cancellationToken);
    }

    internal IOcrProbe Select(string? languageTag)
    {
        // Only the Windows recognizer can decide the language for itself, from the account's display
        // languages. The local models are one network per language and have nothing to fall back on.
        bool isAutomaticLanguage = string.IsNullOrWhiteSpace(languageTag) ||
            string.Equals(languageTag, "auto", StringComparison.OrdinalIgnoreCase);
        if (isAutomaticLanguage)
        {
            return _windows;
        }

        return _backend() switch
        {
            AppOcrBackend.Windows => _windows,
            AppOcrBackend.Local => _local,
            // Windows when it can read this language; the downloaded models otherwise — including
            // when nothing is installed yet, because the local probe's failure names a package the
            // application can go and fetch, while the Windows one names a Features-on-Demand pack it
            // cannot install for the user.
            _ => _availability.StatusFor(languageTag!).Source == OcrLanguageSource.WindowsRecognizer
                ? _windows
                : _local,
        };
    }
}
