using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Probes;

/// <summary>
/// Enumerates real capture targets: top-level application windows (visible, titled,
/// non-tool) and display monitors. Enumeration is performed entirely in-process through
/// Win32 and requires no EngineHost; it is guarded by <see cref="WindowsPlatformGuard"/>
/// so an unsupported platform fails loudly rather than returning fabricated data.
/// </summary>
public sealed class CaptureProbe : ICaptureProbe
{
    private const byte WindowTag = 1;
    private const byte MonitorTag = 2;

    public ValueTask<CaptureProbeResult> ProbeAsync(
        CaptureProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsPlatformGuard.EnsureCurrentSystemSupported();

        var targets = new List<CaptureProbeTarget>();
        EnumerateWindows(targets, cancellationToken);
        EnumerateMonitors(targets, cancellationToken);

        IReadOnlyList<CaptureProbeTarget> filtered = string.IsNullOrWhiteSpace(request.NameFilter)
            ? targets
            : targets.Where(target => target.DisplayName.Contains(
                request.NameFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        return ValueTask.FromResult(new CaptureProbeResult(filtered));
    }

    private static void EnumerateWindows(
        List<CaptureProbeTarget> targets, CancellationToken cancellationToken)
    {
        bool Callback(IntPtr hwnd, IntPtr lParam)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }
            int extendedStyle = NativeMethods.GetWindowLong(
                hwnd, NativeMethods.GwlExStyle);
            if ((extendedStyle & NativeMethods.WsExToolWindow) != 0)
            {
                return true;
            }
            int titleLength = NativeMethods.GetWindowTextLength(hwnd);
            if (titleLength <= 0)
            {
                return true;
            }

            var builder = new StringBuilder(titleLength + 1);
            _ = NativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
            string title = builder.ToString();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            int width = 0;
            int height = 0;
            if (NativeMethods.GetClientRect(hwnd, out NativeMethods.Rect client))
            {
                width = client.Right - client.Left;
                height = client.Bottom - client.Top;
            }
            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);

            ulong handle = unchecked((ulong)hwnd.ToInt64());
            targets.Add(new CaptureProbeTarget(
                new CaptureTargetId(SynthesizeId(handle, WindowTag)),
                title,
                "Window",
                width,
                height,
                dpi == 0 ? 96 : (int)dpi,
                Capturable: width > 0 && height > 0,
                ErrorCode: null)
            {
                NativeHandle = handle,
                ProcessName = TryResolveProcessName(processId),
            });
            return true;
        }

        if (!NativeMethods.EnumWindows(Callback, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            // ERROR_SUCCESS (0) can accompany early callback termination; only surface real failures.
            if (error != 0)
            {
                throw new Win32Exception(error, "Window enumeration failed.");
            }
        }
    }

    private static void EnumerateMonitors(
        List<CaptureProbeTarget> targets, CancellationToken cancellationToken)
    {
        bool Callback(
            IntPtr monitor, IntPtr deviceContext, ref NativeMethods.Rect bounds, IntPtr data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new NativeMethods.MonitorInfoEx
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            int width = info.Monitor.Right - info.Monitor.Left;
            int height = info.Monitor.Bottom - info.Monitor.Top;
            int dpi = 96;
            if (NativeMethods.GetDpiForMonitor(
                    monitor, NativeMethods.MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX != 0)
            {
                dpi = (int)dpiX;
            }
            ulong handle = unchecked((ulong)monitor.ToInt64());
            targets.Add(new CaptureProbeTarget(
                new CaptureTargetId(SynthesizeId(handle, MonitorTag)),
                string.IsNullOrEmpty(info.DeviceName) ? "Display" : info.DeviceName,
                "Monitor",
                width,
                height,
                dpi,
                Capturable: width > 0 && height > 0,
                ErrorCode: null)
            {
                NativeHandle = handle,
                DesktopX = info.Monitor.Left,
                DesktopY = info.Monitor.Top,
            });
            return true;
        }

        if (!NativeMethods.EnumDisplayMonitors(
                IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(error, "Monitor enumeration failed.");
            }
        }
    }

    private static string? TryResolveProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }
        try
        {
            using System.Diagnostics.Process process =
                System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static Guid SynthesizeId(ulong handle, byte kindTag)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, handle);
        bytes[8] = kindTag;
        bytes[15] = 0x5A; // Fixed marker so the identifier is never the empty GUID.
        return new Guid(bytes);
    }

    private static class NativeMethods
    {
        internal const int GwlExStyle = -20;
        internal const int WsExToolWindow = 0x00000080;
        internal const int MdtEffectiveDpi = 0;

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        internal delegate bool MonitorEnumProc(
            IntPtr monitor, IntPtr deviceContext, ref Rect rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MonitorInfoEx
        {
            public uint Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        internal static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(
            IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(
            IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
