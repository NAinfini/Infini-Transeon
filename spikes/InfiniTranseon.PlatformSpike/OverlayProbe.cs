using System.Runtime.InteropServices;
using static InfiniTranseon.PlatformSpike.NativeMethods;

namespace InfiniTranseon.PlatformSpike;

/// <summary>
/// --overlay: a native layered, non-activating, click-through overlay
/// (WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW) rendered with
/// UpdateLayeredWindow via GDI, protected with SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE).
/// Confirms the overlay never becomes foreground and never touches keyboard focus / capture.
/// </summary>
internal static class OverlayProbe
{
    private const string ClassName = "InfiniTranseonSpikeOverlayWindow";
    private const int Width = 520;
    private const int Height = 140;

    private static readonly WndProc s_wndProc = WindowProcedure;

    internal static int Run()
    {
        Console.WriteLine("probe=overlay status=starting durationSeconds=15");

        FocusSnapshot before = FocusSnapshot.Take();
        before.Print("before-overlay");

        nint hInstance = GetModuleHandle(null);
        var wndClass = new WNDCLASSEX
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            HInstance = hInstance,
            HCursor = LoadCursor(0, IDC_ARROW),
            LpszClassName = ClassName,
        };

        if (RegisterClassEx(ref wndClass) == 0)
        {
            Console.Error.WriteLine($"probe=overlay status=error stage=register-class win32={Marshal.GetLastWin32Error()}");
            return ExitCodes.PlatformError;
        }

        int screenWidth = GetSystemMetrics(0 /* SM_CXSCREEN */);
        int x = Math.Max(0, (screenWidth - Width) / 2);
        const int y = 80;

        nint hwnd = CreateWindowEx(
            WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            ClassName, "InfiniTranseon spike overlay", WS_POPUP,
            x, y, Width, Height, 0, 0, hInstance, 0);
        if (hwnd == 0)
        {
            Console.Error.WriteLine($"probe=overlay status=error stage=create-window win32={Marshal.GetLastWin32Error()}");
            _ = UnregisterClass(ClassName, hInstance);
            return ExitCodes.PlatformError;
        }

        bool affinity = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        int affinityError = affinity ? 0 : Marshal.GetLastWin32Error();
        _ = GetWindowDisplayAffinity(hwnd, out uint appliedAffinity);
        Console.WriteLine(
            $"probe=overlay displayAffinityRequested=WDA_EXCLUDEFROMCAPTURE " +
            $"applied={affinity} win32={affinityError} readback=0x{appliedAffinity:X}");

        RenderOverlay(hwnd, x, y);

        // Show WITHOUT activation; combined with WS_EX_NOACTIVATE the overlay must never
        // become the foreground window.
        _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);

        FocusSnapshot during = FocusSnapshot.Take();
        during.Print("during-overlay");
        during.PrintDelta("during-vs-before", before);
        bool overlayStoleForeground = GetForegroundWindow() == hwnd;
        Console.WriteLine($"probe=overlay overlayIsForeground={overlayStoleForeground} (expected=False)");

        MessageLoop.PumpFor(TimeSpan.FromSeconds(15));

        _ = DestroyWindow(hwnd);
        _ = UnregisterClass(ClassName, hInstance);

        FocusSnapshot after = FocusSnapshot.Take();
        after.Print("after-overlay");
        after.PrintDelta("after-vs-before", before);

        Console.WriteLine("probe=overlay status=stopped");
        return overlayStoleForeground ? ExitCodes.FocusViolation : ExitCodes.Success;
    }

    private static nint WindowProcedure(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Renders a translucent status panel into a 32-bit top-down DIB and blits it to the
    /// overlay with UpdateLayeredWindow. Constant per-surface alpha keeps GDI-drawn text
    /// visible without needing per-pixel premultiplied glyph rasterization.
    /// </summary>
    private static void RenderOverlay(nint hwnd, int x, int y)
    {
        nint screenDc = GetDC(0);
        nint memDc = CreateCompatibleDC(screenDc);

        var header = new BITMAPINFOHEADER
        {
            BiSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            BiWidth = Width,
            BiHeight = -Height, // top-down
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = BI_RGB,
        };

        nint dib = CreateDIBSection(memDc, ref header, DIB_RGB_COLORS, out nint bits, 0, 0);
        nint oldBitmap = SelectObject(memDc, dib);

        FillBackground(bits, Width, Height, r: 16, g: 18, b: 24);

        _ = SetBkMode(memDc, TRANSPARENT);
        nint font = CreateFont(
            -28, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, 0, 0, "Segoe UI");
        nint oldFont = SelectObject(memDc, font);

        _ = SetTextColor(memDc, Rgb(240, 240, 245));
        _ = TextOut(memDc, 20, 18, "InfiniTranseon overlay (spike)", 30);
        _ = SetTextColor(memDc, Rgb(120, 210, 160));
        _ = TextOut(memDc, 20, 66, "EngineHost status: sample non-interactive text", 46);

        var dst = new POINT { X = x, Y = y };
        var src = new POINT { X = 0, Y = 0 };
        var size = new SIZE { Cx = Width, Cy = Height };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 210,
            AlphaFormat = 0, // constant alpha across the surface
        };

        bool updated = UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        Console.WriteLine($"probe=overlay updateLayeredWindow={updated} win32={(updated ? 0 : Marshal.GetLastWin32Error())}");

        _ = SelectObject(memDc, oldFont);
        _ = DeleteObject(font);
        _ = SelectObject(memDc, oldBitmap);
        _ = DeleteObject(dib);
        _ = DeleteDC(memDc);
        _ = ReleaseDC(0, screenDc);
    }

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    private static void FillBackground(nint bits, int width, int height, byte r, byte g, byte b)
    {
        int pixelCount = width * height;
        unsafe
        {
            byte* p = (byte*)bits;
            for (int i = 0; i < pixelCount; i++)
            {
                p[0] = b;
                p[1] = g;
                p[2] = r;
                p[3] = 255;
                p += 4;
            }
        }
    }
}
