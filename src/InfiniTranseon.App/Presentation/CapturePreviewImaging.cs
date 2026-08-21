using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace InfiniTranseon.App.Presentation;

/// <summary>
/// Turns the two shapes a capture frame arrives in — the engine's encoded thumbnail and the still
/// frame probe's raw BGRA — into something XAML can show. Every surface that previews a capture
/// target decodes the same way, so the conversion lives here instead of once per page.
/// </summary>
public static class CapturePreviewImaging
{
    public static async Task<ImageSource> FromThumbnailAsync(RuntimeThumbnail thumbnail)
    {
        ArgumentNullException.ThrowIfNull(thumbnail);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(thumbnail.EncodedImage.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    /// <summary>Wraps the probe's raw BGRA in a decodable image. The round trip through the PNG
    /// encoder keeps this on the same WinRT imaging path the rest of the app uses, rather than
    /// depending on the buffer interop extensions that modern .NET no longer ships.</summary>
    public static async Task<ImageSource> FromStillFrameAsync(StillFrameProbeResult frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder =
            await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)frame.PixelWidth,
            (uint)frame.PixelHeight,
            96,
            96,
            frame.BgraPixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
