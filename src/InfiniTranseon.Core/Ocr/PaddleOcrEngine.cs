using System.Runtime.Versioning;
using Microsoft.ML.OnnxRuntime;

namespace InfiniTranseon.Core.Ocr;

/// <summary>
/// One recognized run of text with its pixel bounds inside the image that was read.
/// </summary>
public sealed record PaddleOcrLine(
    string Text,
    double Confidence,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsVertical);

public sealed record PaddleOcrReading(string Text, IReadOnlyList<PaddleOcrLine> Lines);

/// <summary>
/// The complete PP-OCR pipeline for one language: detect text regions, optionally correct their
/// orientation, then recognize each one. Everything runs in-process on ONNX Runtime, so a machine
/// that has already downloaded the package keeps reading text with no network at all.
///
/// Known limitation, stated rather than hidden: a column of vertical CJK text is detected as one
/// tall region and rotated a quarter turn to be read, which is what the reference implementation
/// does. That suits text which is itself rotated; for true vertical layout the glyphs end up on
/// their side and accuracy drops. Fixing it needs a recognition model trained on vertical text, not
/// a change here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PaddleOcrEngine : IDisposable
{
    private readonly PaddleTextDetector _detector;
    private readonly PaddleTextRecognizer _recognizer;

    private PaddleOcrEngine(PaddleTextDetector detector, PaddleTextRecognizer recognizer)
    {
        _detector = detector;
        _recognizer = recognizer;
        LanguageTag = string.Empty;
    }

    public string LanguageTag { get; private init; }

    public static PaddleOcrEngine Load(PaddleOcrModelSet modelSet)
    {
        ArgumentNullException.ThrowIfNull(modelSet);
        InferenceSession? detection = null;
        InferenceSession? classification = null;
        InferenceSession? recognition = null;
        try
        {
            detection = new InferenceSession(modelSet.DetectionModelPath);
            classification = modelSet.ClassificationModelPath is null
                ? null
                : new InferenceSession(modelSet.ClassificationModelPath);
            recognition = new InferenceSession(modelSet.RecognitionModelPath);
            return new PaddleOcrEngine(
                new PaddleTextDetector(detection),
                new PaddleTextRecognizer(recognition, classification))
            {
                LanguageTag = modelSet.LanguageTag,
            };
        }
        catch
        {
            detection?.Dispose();
            classification?.Dispose();
            recognition?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _detector.Dispose();
        _recognizer.Dispose();
    }

    public PaddleOcrReading Read(ReadOnlyMemory<byte> encodedImage)
    {
        RgbImage image = RgbImage.FromEncoded(encodedImage);
        var lines = new List<PaddleOcrLine>();
        foreach (TextBox box in _detector.Detect(image))
        {
            RgbImage crop = image.Crop(box.X, box.Y, box.Width, box.Height);
            if (box.IsTall)
            {
                crop = crop.RotateQuarterTurnCounterClockwise();
            }

            RecognizedText recognized = _recognizer.Recognize(crop);
            if (string.IsNullOrWhiteSpace(recognized.Text))
            {
                continue;
            }

            lines.Add(new PaddleOcrLine(
                recognized.Text,
                recognized.Confidence,
                box.X,
                box.Y,
                box.Width,
                box.Height,
                box.IsTall));
        }

        return new PaddleOcrReading(
            string.Join(Environment.NewLine, lines.Select(line => line.Text)),
            lines);
    }
}
