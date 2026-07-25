namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// The current OCR backend choice, readable without awaiting.
///
/// The setting itself lives in the SQLite settings document, which is read asynchronously, but the
/// probe has to route a recognition synchronously on the path a frame is already waiting on. Rather
/// than block on a database read per frame, <see cref="RealSettingsService"/> publishes the value
/// here every time it loads or saves settings, so this holds whatever was last persisted. Until the
/// first read it holds the same default the settings record does, which means a caller that never
/// touches settings behaves exactly as if the user had left the choice alone.
/// </summary>
public sealed class OcrBackendPreferenceSource
{
    private int _current = (int)AppOcrBackend.Automatic;

    public AppOcrBackend Current => (AppOcrBackend)Volatile.Read(ref _current);

    public void Publish(AppOcrBackend backend) => Volatile.Write(ref _current, (int)backend);
}
