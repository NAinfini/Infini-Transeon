using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Raised when a crop cannot be recognised. Carries a stable code so the UI can explain the specific
/// cause instead of showing a bare exception message.
/// </summary>
public sealed class OcrProbeUnavailableException(string errorCode, string message)
    : InvalidOperationException(message)
{
    /// <summary>No OCR language pack is installed for the profile's source language.</summary>
    public const string LanguageUnavailableCode = "ocr.probe.languageUnavailable";

    /// <summary>Windows has no OCR engine for any of the user profile languages.</summary>
    public const string NoUserProfileLanguageCode = "ocr.probe.noUserProfileLanguage";

    /// <summary>The crop could not be decoded as an image.</summary>
    public const string UndecodableCropCode = "ocr.probe.undecodableCrop";

    /// <summary>A recognised line carried no usable geometry, so it cannot be placed on the overlay.</summary>
    public const string DegenerateLineBoundsCode = "ocr.probe.degenerateLineBounds";

    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Recognises a single crop in-process with <see cref="OcrEngine"/>.
///
/// This is the same engine the runtime uses — <c>src/InfiniTranseon.Engine.Native/src/ocr/windows_media_ocr.cpp</c>
/// calls <c>OcrEngine::TryCreateFromUserProfileLanguages()</c> / <c>TryCreateFromLanguage</c> — so the
/// wizard's "test OCR" result is what the profile will actually read, not an approximation. It exists
/// in the app rather than in Core because Core targets plain <c>net10.0</c> and cannot reference the
/// WinRT projection; the EngineHost protocol (v1) carries no app-to-engine "recognize this image"
/// message, which is why <c>InfiniTranseon.Core.Probes.OcrProbe</c> refuses instead of guessing.
///
/// Every failure is typed and coded. In particular a missing language pack is reported with the
/// languages Windows does have, because the alternative — silently recognising Japanese with an
/// English model — returns confident nonsense rather than an error.
/// </summary>
public sealed class WindowsMediaOcrProbe : IOcrProbe
{
    public async ValueTask<OcrProbeResult> RecognizeAsync(
        OcrProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.EncodedCrop.IsEmpty)
        {
            throw new OcrProbeUnavailableException(
                OcrProbeUnavailableException.UndecodableCropCode,
                "The crop handed to the OCR probe was empty.");
        }

        OcrEngine engine = CreateEngine(request.LanguageTag);
        long startedAt = Stopwatch.GetTimestamp();

        using SoftwareBitmap bitmap = await DecodeAsync(request.EncodedCrop, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken)
            .ConfigureAwait(false);

        double width = bitmap.PixelWidth;
        double height = bitmap.PixelHeight;
        TextLine[] lines = [.. result.Lines.Select(line => ToTextLine(line, width, height))];
        return new OcrProbeResult(
            string.Join(Environment.NewLine, lines.Select(line => line.Text)),
            lines,
            Stopwatch.GetElapsedTime(startedAt),
            engine.RecognizerLanguage.LanguageTag);
    }

    private static OcrEngine CreateEngine(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag) ||
            string.Equals(languageTag, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? throw new OcrProbeUnavailableException(
                    OcrProbeUnavailableException.NoUserProfileLanguageCode,
                    "Windows has no OCR engine for any of this account's display languages. " +
                    "Install an OCR language pack, or set the profile's source language explicitly.");
        }

        return OcrEngine.TryCreateFromLanguage(new Language(languageTag))
            ?? throw new OcrProbeUnavailableException(
                OcrProbeUnavailableException.LanguageUnavailableCode,
                $"Windows has no OCR language pack for '{languageTag}'. Installed: " +
                $"{DescribeAvailableLanguages()}. Install it from Settings › Time & language › " +
                "Language & region, or change the profile's source language.");
    }

    private static string DescribeAvailableLanguages()
    {
        string[] tags = [.. OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag)];
        return tags.Length == 0 ? "(none)" : string.Join(", ", tags);
    }

    private static async Task<SoftwareBitmap> DecodeAsync(
        ReadOnlyMemory<byte> encodedCrop,
        CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(encodedCrop.ToArray().AsBuffer()).AsTask(cancellationToken)
            .ConfigureAwait(false);
        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken)
            .ConfigureAwait(false);
        // OcrEngine only accepts Bgra8; the crop arrives as PNG from the wizard's imaging path.
        return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Converts a recognised line to the runtime's normalized-rect shape.
    /// <see cref="OcrEngine"/> reports no per-line confidence, so <see cref="TextLine.Confidence"/>
    /// is left at zero rather than filled with an invented score.
    /// </summary>
    private static TextLine ToTextLine(OcrLine line, double width, double height)
    {
        double left = 1d;
        double top = 1d;
        double right = 0d;
        double bottom = 0d;
        foreach (OcrWord word in line.Words)
        {
            left = Math.Min(left, word.BoundingRect.X / width);
            top = Math.Min(top, word.BoundingRect.Y / height);
            right = Math.Max(right, (word.BoundingRect.X + word.BoundingRect.Width) / width);
            bottom = Math.Max(bottom, (word.BoundingRect.Y + word.BoundingRect.Height) / height);
        }

        double boxLeft = Math.Clamp(left, 0, 1);
        double boxTop = Math.Clamp(top, 0, 1);
        double boxWidth = Math.Clamp(right - left, 0, 1 - boxLeft);
        double boxHeight = Math.Clamp(bottom - top, 0, 1 - boxTop);
        // A line with no words, or whose words all report an empty rectangle, has nothing the overlay
        // can be positioned over. NormalizedRect rejects a zero extent, and the exception it raises
        // names the "x" parameter, which points at the wrong cause; say what actually happened.
        if (boxWidth <= 0 || boxHeight <= 0)
        {
            throw new OcrProbeUnavailableException(
                OcrProbeUnavailableException.DegenerateLineBoundsCode,
                $"Windows OCR returned the line '{line.Text}' with no measurable bounds " +
                $"({line.Words.Count} word(s)), so it cannot be placed on the overlay.");
        }

        return new TextLine(
            line.Text,
            new NormalizedRect(boxLeft, boxTop, boxWidth, boxHeight),
            Confidence: 0d);
    }
}
