using System.ComponentModel;
using System.Runtime.InteropServices;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Probes;

/// <summary>
/// Raised when a target cannot produce a still frame. Carries a stable code so the UI can explain
/// the specific cause instead of showing an empty preview.
/// </summary>
public sealed class StillFrameUnavailableException(string errorCode, string message)
    : InvalidOperationException(message)
{
    /// <summary>The window rendered nothing — typical of fullscreen-exclusive and some DirectX
    /// games, which never respond to PrintWindow. Monitor capture is the answer for those.</summary>
    public const string TargetRefusedToRenderCode = "capture.stillFrame.targetRefusedToRender";

    /// <summary>The window or monitor handle no longer exists, or reports an empty rectangle.</summary>
    public const string TargetGoneCode = "capture.stillFrame.targetGone";

    /// <summary>The request named a target kind this probe does not handle.</summary>
    public const string UnsupportedKindCode = "capture.stillFrame.unsupportedKind";

    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Grabs one frame from a live window or monitor entirely in-process via GDI, so the setup wizard
/// can show real pixels and run a real OCR test before any profile has been started. The runtime
/// capture path (Graphics Capture inside EngineHost) is unaffected and remains the only thing used
/// for actual translation.
///
/// Deliberate limitation, surfaced rather than hidden: PrintWindow returns an unrendered (black)
/// bitmap for fullscreen-exclusive and some DirectX-composited windows. Those produce
/// <see cref="StillFrameUnavailableException"/> with
/// <see cref="StillFrameUnavailableException.TargetRefusedToRenderCode"/> so the caller can tell
/// the user to capture the monitor instead — a blank preview would look like a bug in this app.
/// </summary>
public sealed class StillFrameProbe : IStillFrameProbe
{
    /// <summary>Guards against a pathological handle reporting an absurd rectangle; the largest
    /// real display today is far below this.</summary>
    private const int MaximumEdge = 32_768;

    public ValueTask<StillFrameProbeResult> CaptureAsync(
        StillFrameProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumLongEdge, 16);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsPlatformGuard.EnsureCurrentSystemSupported();
        if (request.NativeHandle == 0)
        {
            throw new StillFrameUnavailableException(
                StillFrameUnavailableException.TargetGoneCode,
                "The capture target carries no native handle.");
        }

        var handle = (IntPtr)unchecked((long)request.NativeHandle);
        return ValueTask.FromResult(request.Kind switch
        {
            "Window" => CaptureWindow(handle, request.MaximumLongEdge),
            "Monitor" or "Display" => CaptureMonitor(handle, request.MaximumLongEdge),
            _ => throw new StillFrameUnavailableException(
                StillFrameUnavailableException.UnsupportedKindCode,
                $"Still-frame capture does not support target kind '{request.Kind}'."),
        });
    }

    private static StillFrameProbeResult CaptureWindow(IntPtr hwnd, int maximumLongEdge)
    {
        if (!NativeMethods.IsWindow(hwnd) ||
            !NativeMethods.GetClientRect(hwnd, out NativeMethods.Rect client))
        {
            throw new StillFrameUnavailableException(
                StillFrameUnavailableException.TargetGoneCode,
                "The window no longer exists.");
        }

        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        EnsureSaneSize(width, height);

        IntPtr windowDc = NativeMethods.GetDC(hwnd);
        if (windowDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC failed for the window.");
        }
        try
        {
            return RenderToBitmap(
                windowDc,
                width,
                height,
                maximumLongEdge,
                // PW_RENDERFULLCONTENT (2) is what makes this work for DWM-composited and
                // hardware-accelerated windows; without it modern apps come back blank.
                (memoryDc, _) => NativeMethods.PrintWindow(hwnd, memoryDc, 2));
        }
        finally
        {
            _ = NativeMethods.ReleaseDC(hwnd, windowDc);
        }
    }

    private static StillFrameProbeResult CaptureMonitor(IntPtr monitor, int maximumLongEdge)
    {
        var info = new NativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            throw new StillFrameUnavailableException(
                StillFrameUnavailableException.TargetGoneCode,
                "The monitor no longer exists.");
        }

        int width = info.Monitor.Right - info.Monitor.Left;
        int height = info.Monitor.Bottom - info.Monitor.Top;
        EnsureSaneSize(width, height);

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC failed for the desktop.");
        }
        try
        {
            return RenderToBitmap(
                screenDc,
                width,
                height,
                maximumLongEdge,
                // CAPTUREBLT (0x40000000) includes layered windows, which overlays and IMEs use.
                (memoryDc, size) => NativeMethods.BitBlt(
                    memoryDc,
                    0,
                    0,
                    size.Width,
                    size.Height,
                    screenDc,
                    info.Monitor.Left,
                    info.Monitor.Top,
                    NativeMethods.SrcCopy | NativeMethods.CaptureBlt));
        }
        finally
        {
            _ = NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void EnsureSaneSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaximumEdge || height > MaximumEdge)
        {
            throw new StillFrameUnavailableException(
                StillFrameUnavailableException.TargetGoneCode,
                $"The capture target reported an unusable size of {width}x{height}.");
        }
    }

    /// <summary>
    /// Creates a top-down 32bpp DIB, lets <paramref name="render"/> draw the source into it, then
    /// copies out the BGRA bytes. Downscaling happens through StretchBlt with HALFTONE so a 4K
    /// monitor does not hand a 33&#160;MB buffer to the UI thread.
    /// </summary>
    private static StillFrameProbeResult RenderToBitmap(
        IntPtr sourceDc,
        int sourceWidth,
        int sourceHeight,
        int maximumLongEdge,
        Func<IntPtr, (int Width, int Height), bool> render)
    {
        (int width, int height) = Scale(sourceWidth, sourceHeight, maximumLongEdge);
        bool scaled = width != sourceWidth || height != sourceHeight;

        IntPtr memoryDc = NativeMethods.CreateCompatibleDC(sourceDc);
        if (memoryDc == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(), "CreateCompatibleDC failed.");
        }
        IntPtr fullDc = IntPtr.Zero;
        IntPtr fullBitmap = IntPtr.Zero;
        IntPtr fullPrevious = IntPtr.Zero;
        var header = new NativeMethods.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
            Width = width,
            // Negative height requests a top-down DIB, matching the contract's documented layout.
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };
        IntPtr bitmap = NativeMethods.CreateDIBSection(
            memoryDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            _ = NativeMethods.DeleteDC(memoryDc);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDIBSection failed.");
        }
        IntPtr previous = NativeMethods.SelectObject(memoryDc, bitmap);
        try
        {
            if (scaled)
            {
                // Render at native size first: PrintWindow cannot scale, and BitBlt+StretchBlt in
                // one step would drop detail the OCR test needs.
                fullDc = NativeMethods.CreateCompatibleDC(sourceDc);
                if (fullDc == IntPtr.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(), "CreateCompatibleDC failed.");
                }
                var fullHeader = header with { Width = sourceWidth, Height = -sourceHeight };
                fullBitmap = NativeMethods.CreateDIBSection(
                    fullDc, ref fullHeader, 0, out IntPtr fullBits, IntPtr.Zero, 0);
                if (fullBitmap == IntPtr.Zero || fullBits == IntPtr.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(), "CreateDIBSection failed.");
                }
                fullPrevious = NativeMethods.SelectObject(fullDc, fullBitmap);
                RequireRendered(render(fullDc, (sourceWidth, sourceHeight)), fullBits, sourceWidth, sourceHeight);
                _ = NativeMethods.SetStretchBltMode(memoryDc, NativeMethods.Halftone);
                _ = NativeMethods.SetBrushOrgEx(memoryDc, 0, 0, IntPtr.Zero);
                if (!NativeMethods.StretchBlt(
                        memoryDc, 0, 0, width, height,
                        fullDc, 0, 0, sourceWidth, sourceHeight,
                        NativeMethods.SrcCopy))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "StretchBlt failed.");
                }
            }
            else
            {
                RequireRendered(render(memoryDc, (width, height)), bits, width, height);
            }

            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new StillFrameProbeResult(width, height, pixels);
        }
        finally
        {
            if (fullDc != IntPtr.Zero)
            {
                if (fullPrevious != IntPtr.Zero)
                {
                    _ = NativeMethods.SelectObject(fullDc, fullPrevious);
                }
                if (fullBitmap != IntPtr.Zero)
                {
                    _ = NativeMethods.DeleteObject(fullBitmap);
                }
                _ = NativeMethods.DeleteDC(fullDc);
            }
            _ = NativeMethods.SelectObject(memoryDc, previous);
            _ = NativeMethods.DeleteObject(bitmap);
            _ = NativeMethods.DeleteDC(memoryDc);
        }
    }

    /// <summary>
    /// PrintWindow reports success for windows that render nothing, leaving an all-zero bitmap. A
    /// black preview is indistinguishable from a broken app, so an entirely blank frame is treated
    /// as a failure with its own code.
    /// </summary>
    private static void RequireRendered(bool rendered, IntPtr bits, int width, int height)
    {
        if (!rendered)
        {
            throw new StillFrameUnavailableException(
                StillFrameUnavailableException.TargetRefusedToRenderCode,
                "The target did not render a frame.");
        }
        if (IsEntirelyBlank(bits, width, height))
        {
            throw new StillFrameUnavailableException(
                StillFrameUnavailableException.TargetRefusedToRenderCode,
                "The target rendered an empty frame.");
        }
    }

    private static unsafe bool IsEntirelyBlank(IntPtr bits, int width, int height)
    {
        // Sampling beats scanning 8 M pixels on the UI path and cannot produce a false "blank":
        // any non-zero sample proves content. A frame whose sampled points are all zero but which
        // has content elsewhere is possible in principle; the user simply retries, which is far
        // better than silently showing black.
        var scan = (uint*)bits;
        long total = (long)width * height;
        long step = Math.Max(1, total / 4_096);
        for (long index = 0; index < total; index += step)
        {
            if ((scan[index] & 0x00FFFFFF) != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static (int Width, int Height) Scale(int width, int height, int maximumLongEdge)
    {
        int longEdge = Math.Max(width, height);
        if (longEdge <= maximumLongEdge)
        {
            return (width, height);
        }
        double factor = (double)maximumLongEdge / longEdge;
        return (Math.Max(1, (int)Math.Round(width * factor)),
                Math.Max(1, (int)Math.Round(height * factor)));
    }

    private static class NativeMethods
    {
        internal const int SrcCopy = 0x00CC0020;
        internal const int CaptureBlt = 0x40000000;
        internal const int Halftone = 4;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            public uint Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal record struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ColorUsed;
            public uint ColorImportant;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr hwnd, IntPtr deviceContext);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PrintWindow(IntPtr hwnd, IntPtr deviceContext, uint flags);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateDIBSection(
            IntPtr deviceContext,
            ref BitmapInfoHeader header,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr handle);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(
            IntPtr destination,
            int x,
            int y,
            int width,
            int height,
            IntPtr source,
            int sourceX,
            int sourceY,
            int rasterOperation);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool StretchBlt(
            IntPtr destination,
            int x,
            int y,
            int width,
            int height,
            IntPtr source,
            int sourceX,
            int sourceY,
            int sourceWidth,
            int sourceHeight,
            int rasterOperation);

        [DllImport("gdi32.dll")]
        internal static extern int SetStretchBltMode(IntPtr deviceContext, int mode);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetBrushOrgEx(
            IntPtr deviceContext, int x, int y, IntPtr previous);
    }
}
