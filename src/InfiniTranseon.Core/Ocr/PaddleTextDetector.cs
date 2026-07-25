using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace InfiniTranseon.Core.Ocr;

/// <summary>An axis-aligned text box in source-image pixels, with its mean detection probability.</summary>
internal readonly record struct TextBox(int X, int Y, int Width, int Height, double Score)
{
    internal bool IsTall => Height > Width * 1.5;
}

/// <summary>
/// PP-OCR differentiable-binarization detection: the network emits a per-pixel text probability map
/// and the boxes are recovered from it here.
///
/// Documented simplification: regions are reduced to axis-aligned boxes taken from 8-connected
/// components, not to rotated minimum-area quadrilaterals as the reference implementation does.
/// Game text is overwhelmingly axis-aligned, and the rotated path needs convex hulls and Vatti
/// polygon offsetting for an accuracy gain this product cannot currently measure. The cost is real
/// and is stated rather than hidden: text rotated more than a few degrees yields a box padded with
/// background, which the recognizer reads less reliably. Vertical runs are handled — see
/// <see cref="TextBox.IsTall"/> — because vertical Japanese is common.
/// </summary>
internal sealed class PaddleTextDetector(InferenceSession session) : IDisposable
{
    /// <summary>Longest side fed to the network. Larger finds smaller glyphs and costs quadratically.</summary>
    private const int MaximumSide = 960;

    /// <summary>The network is fully convolutional with stride 32, so both sides must be multiples.</summary>
    private const int SizeMultiple = 32;

    private const float BinaryThreshold = 0.3f;
    private const double BoxScoreThreshold = 0.5;
    private const double UnclipRatio = 1.5;
    private const int MinimumBoxSide = 3;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] StandardDeviation = [0.229f, 0.224f, 0.225f];

    private readonly InferenceSession _session = session;

    public void Dispose() => _session.Dispose();

    internal IReadOnlyList<TextBox> Detect(RgbImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        (int width, int height) = ResolveNetworkSize(image.Width, image.Height);
        RgbImage resized = image.Resize(width, height);

        var input = new DenseTensor<float>([1, 3, height, width]);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int source = ((y * width) + x) * 3;
                for (int channel = 0; channel < 3; channel++)
                {
                    input[0, channel, y, x] =
                        ((resized.Pixels[source + channel] / 255f) - Mean[channel]) /
                        StandardDeviation[channel];
                }
            }
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(
            [NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), input)]);
        Tensor<float> probabilities = outputs.First().AsTensor<float>();

        double scaleX = (double)image.Width / width;
        double scaleY = (double)image.Height / height;
        return ExtractBoxes(probabilities, width, height, scaleX, scaleY, image.Width, image.Height);
    }

    /// <summary>
    /// Scales the longest side down to <see cref="MaximumSide"/> when necessary, then rounds both
    /// sides up to the stride. Images already smaller are left at their own size rather than
    /// upscaled, which would only invent detail.
    /// </summary>
    private static (int Width, int Height) ResolveNetworkSize(int width, int height)
    {
        double scale = Math.Min(1.0, (double)MaximumSide / Math.Max(width, height));
        int scaledWidth = Math.Max(SizeMultiple, (int)Math.Round(width * scale));
        int scaledHeight = Math.Max(SizeMultiple, (int)Math.Round(height * scale));
        return (
            (int)(Math.Ceiling(scaledWidth / (double)SizeMultiple) * SizeMultiple),
            (int)(Math.Ceiling(scaledHeight / (double)SizeMultiple) * SizeMultiple));
    }

    private static List<TextBox> ExtractBoxes(
        Tensor<float> probabilities,
        int width,
        int height,
        double scaleX,
        double scaleY,
        int sourceWidth,
        int sourceHeight)
    {
        var labels = new int[width * height];
        var boxes = new List<TextBox>();
        var queue = new Queue<int>();
        int nextLabel = 0;

        for (int start = 0; start < labels.Length; start++)
        {
            if (labels[start] != 0 || probabilities[0, 0, start / width, start % width] < BinaryThreshold)
            {
                continue;
            }

            nextLabel++;
            labels[start] = nextLabel;
            queue.Enqueue(start);
            int minX = width, minY = height, maxX = -1, maxY = -1;
            double total = 0;
            int count = 0;

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int x = index % width;
                int y = index / width;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                total += probabilities[0, 0, y, x];
                count++;

                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        int neighbour = (ny * width) + nx;
                        if (labels[neighbour] != 0 ||
                            probabilities[0, 0, ny, nx] < BinaryThreshold)
                        {
                            continue;
                        }

                        labels[neighbour] = nextLabel;
                        queue.Enqueue(neighbour);
                    }
                }
            }

            double score = total / count;
            if (score < BoxScoreThreshold)
            {
                continue;
            }

            if (Unclip(minX, minY, maxX, maxY) is not { } box)
            {
                continue;
            }

            int left = (int)Math.Floor(box.X * scaleX);
            int top = (int)Math.Floor(box.Y * scaleY);
            int right = (int)Math.Ceiling((box.X + box.Width) * scaleX);
            int bottom = (int)Math.Ceiling((box.Y + box.Height) * scaleY);
            left = Math.Clamp(left, 0, sourceWidth - 1);
            top = Math.Clamp(top, 0, sourceHeight - 1);
            right = Math.Clamp(right, left + 1, sourceWidth);
            bottom = Math.Clamp(bottom, top + 1, sourceHeight);
            if (right - left < MinimumBoxSide || bottom - top < MinimumBoxSide)
            {
                continue;
            }

            boxes.Add(new TextBox(left, top, right - left, bottom - top, score));
        }

        // Reading order: top to bottom, then left to right, with a tolerance so that glyphs on the
        // same visual line are not reordered by a few pixels of baseline jitter.
        boxes.Sort((left, right) =>
        {
            int rowDelta = left.Y - right.Y;
            return Math.Abs(rowDelta) > Math.Max(left.Height, right.Height) / 2
                ? rowDelta
                : left.X - right.X;
        });
        return boxes;
    }

    /// <summary>
    /// Grows the region by the standard differentiable-binarization offset, area × ratio ÷ perimeter.
    /// The network is trained on shrunk polygons, so an unexpanded box clips the glyph edges.
    /// </summary>
    private static (int X, int Y, int Width, int Height)? Unclip(int minX, int minY, int maxX, int maxY)
    {
        double width = maxX - minX + 1;
        double height = maxY - minY + 1;
        if (width < MinimumBoxSide || height < MinimumBoxSide)
        {
            return null;
        }

        double distance = width * height * UnclipRatio / (2 * (width + height));
        int offset = (int)Math.Round(distance);
        return (minX - offset, minY - offset, (int)width + (offset * 2), (int)height + (offset * 2));
    }
}
