using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Settings;

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
/// it: the user can add a Windows language pack, and an explicitly approved local-model install can
/// complete while the app is open.
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
        OcrBackendPreference preference = _backend() switch
        {
            AppOcrBackend.Windows => OcrBackendPreference.Windows,
            AppOcrBackend.Local => OcrBackendPreference.Local,
            _ => OcrBackendPreference.Automatic,
        };
        return OcrRuntimeBackendResolver.Resolve(preference, _availability, languageTag) switch
        {
            RuntimeOcrBackend.Windows => _windows,
            RuntimeOcrBackend.Local => _local,
            _ => throw new InvalidOperationException("Global OCR preference cannot select cloud OCR."),
        };
    }
}

public static class OcrRuntimeBackendResolver
{
    public static RuntimeOcrBackend Resolve(
        OcrBackendPreference preference,
        IOcrLanguageAvailability availability,
        string? languageTag)
    {
        ArgumentNullException.ThrowIfNull(availability);
        if (!Enum.IsDefined(preference))
            throw new ArgumentOutOfRangeException(nameof(preference));
        if (string.IsNullOrWhiteSpace(languageTag) ||
            string.Equals(languageTag, "auto", StringComparison.OrdinalIgnoreCase))
            return RuntimeOcrBackend.Windows;
        return preference switch
        {
            OcrBackendPreference.Windows => RuntimeOcrBackend.Windows,
            OcrBackendPreference.Local => RuntimeOcrBackend.Local,
            _ => availability.StatusFor(languageTag).Source ==
                OcrLanguageSource.WindowsRecognizer
                    ? RuntimeOcrBackend.Windows
                    : RuntimeOcrBackend.Local,
        };
    }
}
