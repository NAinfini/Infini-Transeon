using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace InfiniTranseon.Core.Ocr;

internal readonly record struct RecognizedText(string Text, double Confidence);

/// <summary>
/// PP-OCR recognition: an optional 0°/180° orientation classifier followed by a CRNN whose CTC
/// output is greedily decoded against the character set the model itself carries.
///
/// The character set is read from the ONNX <c>character</c> metadata entry rather than from a
/// side-car dictionary file. This was verified against the shipped models: the Japanese recognizer
/// declares 4400 characters and 4401 output classes, and the English one 96 and 97. That one-class
/// surplus is the CTC blank at index 0, so character <c>i</c> occupies class <c>i + 1</c>. A model
/// whose arithmetic disagrees is rejected at construction instead of silently decoding to gibberish.
/// </summary>
internal sealed class PaddleTextRecognizer : IDisposable
{
    /// <summary>Fixed input height of the PP-OCRv4 recognition network.</summary>
    private const int InputHeight = 48;

    private const int MinimumWidth = 16;
    private const int MaximumWidth = 1600;

    /// <summary>Above this the classifier's 180° verdict is trusted; below it the crop is left alone.</summary>
    private const float OrientationConfidence = 0.9f;

    private readonly InferenceSession _recognizer;
    private readonly InferenceSession? _orientation;
    private readonly string[] _characters;

    internal PaddleTextRecognizer(InferenceSession recognizer, InferenceSession? orientation)
    {
        ArgumentNullException.ThrowIfNull(recognizer);
        _recognizer = recognizer;
        _orientation = orientation;

        if (!recognizer.ModelMetadata.CustomMetadataMap.TryGetValue("character", out string? charset) ||
            string.IsNullOrEmpty(charset))
        {
            throw new InvalidDataException(
                "The recognition model carries no 'character' metadata, so its output classes cannot " +
                "be mapped to text.");
        }

        // PaddleOCR appends the space character to the dictionary when a model is trained with
        // use_space_char, and the export writes that entry as a bare newline, so the split yields an
        // empty string where a space belongs. Decoding it literally silently deletes every space
        // from the output — "Save your progress?" came back as "Saveyourprogress?" before this line.
        // An empty entry is never a real character, so substituting a space is unambiguous.
        _characters = [.. charset.Split('\n').Select(entry => entry.Length == 0 ? " " : entry)];
        int classes = recognizer.OutputMetadata.Values.First().Dimensions[^1];
        if (classes != _characters.Length + 1)
        {
            throw new InvalidDataException(
                $"The recognition model declares {_characters.Length} characters but emits {classes} " +
                "classes; exactly one blank class was expected. Refusing to decode against a " +
                "mismatched character set.");
        }
    }

    public void Dispose()
    {
        _recognizer.Dispose();
        _orientation?.Dispose();
    }

    internal RecognizedText Recognize(RgbImage crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        RgbImage upright = _orientation is null || !IsUpsideDown(crop)
            ? crop
            : crop.RotateHalfTurn();

        int width = Math.Clamp(
            (int)Math.Ceiling(InputHeight * (double)upright.Width / upright.Height),
            MinimumWidth,
            MaximumWidth);
        RgbImage resized = upright.Resize(width, InputHeight);

        var input = new DenseTensor<float>([1, 3, InputHeight, width]);
        Normalize(resized, input);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _recognizer.Run(
            [NamedOnnxValue.CreateFromTensor(_recognizer.InputMetadata.Keys.First(), input)]);
        return Decode(outputs.First().AsTensor<float>());
    }

    private bool IsUpsideDown(RgbImage crop)
    {
        RgbImage resized = crop.Resize(192, InputHeight);
        var input = new DenseTensor<float>([1, 3, InputHeight, 192]);
        Normalize(resized, input);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _orientation!.Run(
            [NamedOnnxValue.CreateFromTensor(_orientation.InputMetadata.Keys.First(), input)]);
        Tensor<float> scores = outputs.First().AsTensor<float>();
        return scores[0, 1] > OrientationConfidence;
    }

    /// <summary>Scales to [-1, 1], which is what the recognition and classification heads expect.</summary>
    private static void Normalize(RgbImage image, DenseTensor<float> destination)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int source = ((y * image.Width) + x) * 3;
                for (int channel = 0; channel < 3; channel++)
                {
                    destination[0, channel, y, x] =
                        ((image.Pixels[source + channel] / 255f) - 0.5f) / 0.5f;
                }
            }
        }
    }

    /// <summary>
    /// Greedy CTC: take the strongest class at each timestep, collapse runs of the same class, and
    /// drop the blank. Confidence is the mean probability of the timesteps that produced a character.
    /// </summary>
    private RecognizedText Decode(Tensor<float> logits)
    {
        int steps = logits.Dimensions[1];
        int classes = logits.Dimensions[2];
        var text = new System.Text.StringBuilder();
        double total = 0;
        int emitted = 0;
        int previous = -1;

        for (int step = 0; step < steps; step++)
        {
            int best = 0;
            float bestScore = logits[0, step, 0];
            for (int candidate = 1; candidate < classes; candidate++)
            {
                if (logits[0, step, candidate] > bestScore)
                {
                    bestScore = logits[0, step, candidate];
                    best = candidate;
                }
            }

            if (best != 0 && best != previous)
            {
                text.Append(_characters[best - 1]);
                total += bestScore;
                emitted++;
            }

            previous = best;
        }

        return new RecognizedText(text.ToString(), emitted == 0 ? 0 : total / emitted);
    }
}
