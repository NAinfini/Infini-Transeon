using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace InfiniTranseon.Core.Ocr;

/// <summary>
/// A tightly packed 24-bit RGB raster. PP-OCR wants RGB planes in NCHW order, so carrying pixels in
/// this shape keeps the tensor builders free of stride and channel-order arithmetic.
///
/// Resampling is implemented here rather than delegated to GDI+: the results must be identical on
/// every machine for the recognizer's output to be reproducible, and GDI+ interpolation is neither
/// specified nor stable across Windows versions.
/// </summary>
internal sealed class RgbImage
{
    internal RgbImage(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 3)];
    }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>Row-major, three bytes per pixel, red first.</summary>
    internal byte[] Pixels { get; }

    /// <summary>Decodes any raster format GDI+ understands; the probe contract carries PNG.</summary>
    [SupportedOSPlatform("windows")]
    internal static RgbImage FromEncoded(ReadOnlyMemory<byte> encoded)
    {
        using var stream = new MemoryStream(encoded.ToArray(), writable: false);
        using var bitmap = new Bitmap(stream);
        var image = new RgbImage(bitmap.Width, bitmap.Height);
        BitmapData locked = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < image.Height; y++)
                {
                    byte* row = (byte*)locked.Scan0 + ((long)y * locked.Stride);
                    int destination = y * image.Width * 3;
                    for (int x = 0; x < image.Width; x++)
                    {
                        // GDI's 24bpp "RGB" is physically BGR.
                        image.Pixels[destination + (x * 3)] = row[(x * 3) + 2];
                        image.Pixels[destination + (x * 3) + 1] = row[(x * 3) + 1];
                        image.Pixels[destination + (x * 3) + 2] = row[x * 3];
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        return image;
    }

    /// <summary>Bilinear resample with half-pixel centres, matching the reference preprocessing.</summary>
    internal RgbImage Resize(int width, int height)
    {
        var result = new RgbImage(width, height);
        double scaleX = (double)Width / width;
        double scaleY = (double)Height / height;
        for (int y = 0; y < height; y++)
        {
            double sourceY = ((y + 0.5) * scaleY) - 0.5;
            int y0 = (int)Math.Floor(sourceY);
            double weightY = sourceY - y0;
            int top = Math.Clamp(y0, 0, Height - 1);
            int bottom = Math.Clamp(y0 + 1, 0, Height - 1);
            for (int x = 0; x < width; x++)
            {
                double sourceX = ((x + 0.5) * scaleX) - 0.5;
                int x0 = (int)Math.Floor(sourceX);
                double weightX = sourceX - x0;
                int left = Math.Clamp(x0, 0, Width - 1);
                int right = Math.Clamp(x0 + 1, 0, Width - 1);
                int destination = ((y * width) + x) * 3;
                for (int channel = 0; channel < 3; channel++)
                {
                    double topLeft = Pixels[(((top * Width) + left) * 3) + channel];
                    double topRight = Pixels[(((top * Width) + right) * 3) + channel];
                    double bottomLeft = Pixels[(((bottom * Width) + left) * 3) + channel];
                    double bottomRight = Pixels[(((bottom * Width) + right) * 3) + channel];
                    double upper = topLeft + ((topRight - topLeft) * weightX);
                    double lower = bottomLeft + ((bottomRight - bottomLeft) * weightX);
                    result.Pixels[destination + channel] =
                        (byte)Math.Clamp(Math.Round(upper + ((lower - upper) * weightY)), 0, 255);
                }
            }
        }

        return result;
    }

    internal RgbImage Crop(int x, int y, int width, int height)
    {
        int left = Math.Clamp(x, 0, Width - 1);
        int top = Math.Clamp(y, 0, Height - 1);
        int clampedWidth = Math.Clamp(width, 1, Width - left);
        int clampedHeight = Math.Clamp(height, 1, Height - top);
        var result = new RgbImage(clampedWidth, clampedHeight);
        for (int row = 0; row < clampedHeight; row++)
        {
            Array.Copy(
                Pixels,
                (((top + row) * Width) + left) * 3,
                result.Pixels,
                row * clampedWidth * 3,
                clampedWidth * 3);
        }

        return result;
    }

    /// <summary>Rotates a quarter turn counter-clockwise, used to lay vertical text out horizontally.</summary>
    internal RgbImage RotateQuarterTurnCounterClockwise()
    {
        var result = new RgbImage(Height, Width);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int source = ((y * Width) + x) * 3;
                int destination = ((((Width - 1 - x) * Height) + y) * 3);
                result.Pixels[destination] = Pixels[source];
                result.Pixels[destination + 1] = Pixels[source + 1];
                result.Pixels[destination + 2] = Pixels[source + 2];
            }
        }

        return result;
    }

    internal RgbImage RotateHalfTurn()
    {
        var result = new RgbImage(Width, Height);
        int count = Width * Height;
        for (int index = 0; index < count; index++)
        {
            int source = index * 3;
            int destination = (count - 1 - index) * 3;
            result.Pixels[destination] = Pixels[source];
            result.Pixels[destination + 1] = Pixels[source + 1];
            result.Pixels[destination + 2] = Pixels[source + 2];
        }

        return result;
    }
}
