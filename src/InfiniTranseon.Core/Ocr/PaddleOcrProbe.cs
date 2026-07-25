using System.Diagnostics;
using System.Runtime.Versioning;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Ocr;

/// <summary>
/// Raised when a crop cannot be read with the local models. It carries a stable code so the caller
/// can tell "this language was never downloaded" apart from "the package on disk is broken", which
/// are the same sentence to a user but completely different problems to fix.
/// </summary>
public sealed class PaddleOcrUnavailableException(string errorCode, string message)
    : InvalidOperationException(message)
{
    public const string LanguageNotInstalledCode = "ocr.paddle.languageNotInstalled";
    public const string AutoLanguageUnsupportedCode = "ocr.paddle.autoLanguageUnsupported";

    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Recognizes a caller-supplied crop with the downloaded PP-OCR models. Sessions are expensive to
/// create and cheap to keep, so one engine per language is loaded on first use and held until the
/// probe is disposed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PaddleOcrProbe(IPaddleOcrModelCatalog catalog) : IOcrProbe, IDisposable
{
    private readonly IPaddleOcrModelCatalog _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));

    private readonly Dictionary<string, PaddleOcrEngine> _engines = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (PaddleOcrEngine engine in _engines.Values)
        {
            engine.Dispose();
        }

        _engines.Clear();
        _gate.Dispose();
    }

    public async ValueTask<OcrProbeResult> RecognizeAsync(
        OcrProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.LanguageTag) ||
            string.Equals(request.LanguageTag, "auto", StringComparison.OrdinalIgnoreCase))
        {
            throw new PaddleOcrUnavailableException(
                PaddleOcrUnavailableException.AutoLanguageUnsupportedCode,
                "The local OCR models are per-language and cannot detect the language themselves. " +
                "Choose the source language, or use the Windows recognizer, which follows the " +
                "account's display languages.");
        }

        PaddleOcrEngine engine = await ResolveEngineAsync(request.LanguageTag, cancellationToken)
            .ConfigureAwait(false);

        long start = Stopwatch.GetTimestamp();
        // Recognition is CPU-bound and takes tens to hundreds of milliseconds; running it inline
        // would block whichever thread the caller awaited on, which for the wizard is the UI thread.
        PaddleOcrReading reading = await Task.Run(
            () => engine.Read(request.EncodedCrop), cancellationToken).ConfigureAwait(false);
        TimeSpan latency = Stopwatch.GetElapsedTime(start);

        return new OcrProbeResult(
            reading.Text,
            [.. reading.Lines.Select(line => ToTextLine(line, request.PixelWidth, request.PixelHeight))],
            latency,
            engine.LanguageTag);
    }

    private async ValueTask<PaddleOcrEngine> ResolveEngineAsync(
        string languageTag,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_catalog.TryResolve(languageTag, out PaddleOcrModelSet? modelSet))
            {
                throw new PaddleOcrUnavailableException(
                    PaddleOcrUnavailableException.LanguageNotInstalledCode,
                    $"No local OCR model is installed for '{languageTag}'.");
            }

            if (!_engines.TryGetValue(modelSet.LanguageTag, out PaddleOcrEngine? engine))
            {
                engine = PaddleOcrEngine.Load(modelSet);
                _engines[modelSet.LanguageTag] = engine;
            }

            return engine;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Converts pixel bounds to the normalized rectangle the runtime contract uses. The crop the
    /// caller encoded and the pixel size it declared must agree; when they do not the bounds are
    /// clamped into range rather than throwing, because a slightly wrong rectangle is still a usable
    /// result and the text is what the caller asked for.
    /// </summary>
    private static TextLine ToTextLine(PaddleOcrLine line, int pixelWidth, int pixelHeight)
    {
        const double minimum = 1e-6;
        double imageWidth = Math.Max(pixelWidth, 1);
        double imageHeight = Math.Max(pixelHeight, 1);
        double x = Math.Clamp(line.X / imageWidth, 0, 1 - minimum);
        double y = Math.Clamp(line.Y / imageHeight, 0, 1 - minimum);
        double boxWidth = Math.Clamp(line.Width / imageWidth, minimum, 1 - x);
        double boxHeight = Math.Clamp(line.Height / imageHeight, minimum, 1 - y);

        return new TextLine(line.Text, new NormalizedRect(x, y, boxWidth, boxHeight), line.Confidence)
        {
            IsVertical = line.IsVertical,
        };
    }
}
