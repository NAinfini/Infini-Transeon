using System.Runtime.InteropServices;
using static InfiniTranseon.PlatformSpike.NativeMethods;

namespace InfiniTranseon.PlatformSpike;

/// <summary>
/// --focus-probe: opens a plain Win32 "settings window" stand-in, closes it to the tray, and
/// records foreground/focus/capture at each transition. Then brings up the non-activating
/// overlay alongside and confirms the whole sequence never steals foreground from whatever
/// window the tester had active before the probe began.
/// </summary>
internal static class FocusProbe
{
    private const string SettingsClassName = "InfiniTranseonSpikeSettingsWindow";
    private const string OverlayClassName = "InfiniTranseonSpikeFocusOverlayWindow";
    private const uint TrayIconId = 0x2001;

    private static readonly WndProc s_settingsProc = SettingsProcedure;
    private static readonly WndProc s_overlayProc = OverlayProcedure;

    internal static int Run()
    {
        Console.WriteLine("probe=focus status=starting");

        // The tester's genuinely-active window, captured before we create anything.
        FocusSnapshot testerBaseline = FocusSnapshot.Take();
        testerBaseline.Print("tester-baseline");

        nint hInstance = GetModuleHandle(null);

        if (!TryRegister(SettingsClassName, s_settingsProc, hInstance) ||
            !TryRegister(OverlayClassName, s_overlayProc, hInstance))
        {
            return ExitCodes.PlatformError;
        }

        // Transition 1: open the settings window. A settings window legitimately activates.
        nint settings = CreateWindowEx(
            0, SettingsClassName, "InfiniTranseon Settings (spike)", WS_OVERLAPPEDWINDOW,
            120, 120, 720, 480, 0, 0, hInstance, 0);
        if (settings == 0)
        {
            Console.Error.WriteLine($"probe=focus status=error stage=create-settings win32={Marshal.GetLastWin32Error()}");
            _ = UnregisterClass(SettingsClassName, hInstance);
            _ = UnregisterClass(OverlayClassName, hInstance);
            return ExitCodes.PlatformError;
        }

        _ = ShowWindow(settings, SW_SHOW);
        _ = SetForegroundWindow(settings);
        MessageLoop.PumpFor(TimeSpan.FromSeconds(2));
        FocusSnapshot afterOpen = FocusSnapshot.Take();
        afterOpen.Print("after-settings-open");
        afterOpen.PrintDelta("after-settings-open-vs-tester", testerBaseline);

        // Transition 2: close the settings window to the tray (hide + Shell_NotifyIcon).
        var iconData = new NOTIFYICONDATA
        {
            CbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            HWnd = settings,
            UID = TrayIconId,
            UFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            UCallbackMessage = WM_TRAYCALLBACK,
            HIcon = LoadIcon(0, IDI_APPLICATION),
            SzTip = "InfiniTranseon spike (closed to tray)",
            SzInfo = string.Empty,
            SzInfoTitle = string.Empty,
            UVersion = NOTIFYICON_VERSION_4,
        };
        bool trayAdded = Shell_NotifyIcon(NIM_ADD, ref iconData);
        _ = ShowWindow(settings, SW_HIDE);
        MessageLoop.PumpFor(TimeSpan.FromSeconds(1));
        FocusSnapshot afterClose = FocusSnapshot.Take();
        afterClose.Print("after-close-to-tray");
        afterClose.PrintDelta("after-close-to-tray-vs-tester", testerBaseline);
        Console.WriteLine($"probe=focus trayAdded={trayAdded}");

        // Transition 3: run the non-activating overlay alongside the tray-resident settings.
        int screenWidth = GetSystemMetrics(0 /* SM_CXSCREEN */);
        nint overlay = CreateWindowEx(
            WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            OverlayClassName, "InfiniTranseon spike focus overlay", WS_POPUP,
            Math.Max(0, screenWidth - 360), 60, 320, 90, 0, 0, hInstance, 0);
        if (overlay != 0)
        {
            _ = SetWindowDisplayAffinity(overlay, WDA_EXCLUDEFROMCAPTURE);
            _ = ShowWindow(overlay, SW_SHOWNOACTIVATE);
            MessageLoop.PumpFor(TimeSpan.FromSeconds(1));
        }

        FocusSnapshot withOverlay = FocusSnapshot.Take();
        withOverlay.Print("settings-in-tray-plus-overlay");
        withOverlay.PrintDelta("with-overlay-vs-tester", testerBaseline);

        bool overlayStoleForeground = overlay != 0 && GetForegroundWindow() == overlay;
        Console.WriteLine($"probe=focus overlayIsForeground={overlayStoleForeground} (expected=False)");

        // Cleanup.
        _ = Shell_NotifyIcon(NIM_DELETE, ref iconData);
        if (overlay != 0)
        {
            _ = DestroyWindow(overlay);
        }

        _ = DestroyWindow(settings);
        _ = UnregisterClass(SettingsClassName, hInstance);
        _ = UnregisterClass(OverlayClassName, hInstance);

        Console.WriteLine("probe=focus status=stopped");
        return overlayStoleForeground ? ExitCodes.FocusViolation : ExitCodes.Success;
    }

    private static bool TryRegister(string className, WndProc proc, nint hInstance)
    {
        var wndClass = new WNDCLASSEX
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            HInstance = hInstance,
            HIcon = LoadIcon(0, IDI_APPLICATION),
            HCursor = LoadCursor(0, IDC_ARROW),
            LpszClassName = className,
        };

        if (RegisterClassEx(ref wndClass) == 0)
        {
            Console.Error.WriteLine($"probe=focus status=error stage=register-class class={className} win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        return true;
    }

    private static nint SettingsProcedure(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static nint OverlayProcedure(nint hWnd, uint msg, nint wParam, nint lParam)
        => DefWindowProc(hWnd, msg, wParam, lParam);
}
