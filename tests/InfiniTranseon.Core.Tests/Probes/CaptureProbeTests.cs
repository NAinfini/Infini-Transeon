using System.Diagnostics;
using System.Runtime.InteropServices;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Core.Probes;

namespace InfiniTranseon.Core.Tests.Probes;

public sealed class CaptureProbeTests
{
    [Fact]
    public async Task EnumeratesAtLeastOneMonitor()
    {
        var probe = new CaptureProbe();

        CaptureProbeResult result = await probe.ProbeAsync(
            new CaptureProbeRequest(NameFilter: null), TestContext.Current.CancellationToken);

        IReadOnlyList<CaptureProbeTarget> monitors =
            result.Targets.Where(target => target.Kind == "Monitor").ToArray();
        Assert.NotEmpty(monitors);
        Assert.All(monitors, monitor =>
        {
            Assert.NotEqual(Guid.Empty, monitor.TargetId.Value);
            Assert.NotEqual(0UL, monitor.NativeHandle);
        });
        Assert.Equal(
            result.Targets.Select(target => target.TargetId).Distinct().Count(),
            result.Targets.Count);
    }

    [Fact]
    public async Task ReturnsOwnProcessWindowWithHandleAndProcessName()
    {
        string title = "InfiniTranseon-CaptureProbe-" + Guid.NewGuid().ToString("n");
        using var window = TestWindow.TryCreate(title);
        if (window is null)
        {
            Assert.Skip("No interactive window station available to host a top-level window.");
            return;
        }

        var probe = new CaptureProbe();
        CaptureProbeResult result = await probe.ProbeAsync(
            new CaptureProbeRequest(title), TestContext.Current.CancellationToken);

        CaptureProbeTarget? match = result.Targets
            .FirstOrDefault(target => target.DisplayName == title);
        if (match is null)
        {
            Assert.Skip("Created window was not enumerated on this session (headless host).");
            return;
        }

        Assert.Equal("Window", match.Kind);
        Assert.Equal(window.Handle, match.NativeHandle);
        Assert.Equal(
            Process.GetCurrentProcess().ProcessName,
            match.ProcessName);
    }

    private sealed class TestWindow : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly ushort _classAtom;
        private readonly IntPtr _instance;
        // Kept alive for the window's lifetime so the marshalled thunk is not collected.
        private readonly Native.WndProc _wndProc;

        private TestWindow(IntPtr hwnd, ushort classAtom, IntPtr instance, Native.WndProc wndProc)
        {
            _hwnd = hwnd;
            _classAtom = classAtom;
            _instance = instance;
            _wndProc = wndProc;
        }

        public ulong Handle => unchecked((ulong)_hwnd.ToInt64());

        public static TestWindow? TryCreate(string title)
        {
            IntPtr instance = Native.GetModuleHandle(null);
            Native.WndProc wndProc = Native.DefWindowProc;
            string className = "InfiniTranseonProbeClass-" + Guid.NewGuid().ToString("n");
            var windowClass = new Native.WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<Native.WndClassEx>(),
                lpfnWndProc = wndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            ushort atom = Native.RegisterClassEx(ref windowClass);
            if (atom == 0)
            {
                return null;
            }

            IntPtr hwnd = Native.CreateWindowEx(
                0, className, title, Native.WsOverlappedWindow | Native.WsVisible,
                100, 100, 320, 240, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Native.UnregisterClass(className, instance);
                return null;
            }

            Native.ShowWindow(hwnd, Native.SwShowNormal);
            return new TestWindow(hwnd, atom, instance, wndProc);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                Native.DestroyWindow(_hwnd);
            }
            Native.UnregisterClass(new IntPtr(_classAtom), _instance);
            GC.KeepAlive(_wndProc);
        }
    }

    private static class Native
    {
        internal const int WsVisible = unchecked((int)0x10000000);
        internal const int WsOverlappedWindow = 0x00CF0000;
        internal const int SwShowNormal = 1;

        internal delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WndClassEx
        {
            public uint cbSize;
            public uint style;
            [MarshalAs(UnmanagedType.FunctionPtr)]
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WndClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true)]
        internal static extern bool UnregisterClass(IntPtr classAtom, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr DefWindowProc(
            IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
