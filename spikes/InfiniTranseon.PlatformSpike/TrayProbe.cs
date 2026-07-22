using System.Runtime.InteropServices;
using static InfiniTranseon.PlatformSpike.NativeMethods;

namespace InfiniTranseon.PlatformSpike;

/// <summary>
/// --tray: Win32 Shell_NotifyIcon tray icon owned by a hidden message-only HWND, with a
/// NATIVE TrackPopupMenuEx popup on right-click. Instruments whether showing/dismissing the
/// menu perturbs the foreground / keyboard-focus / mouse-capture windows of the active app.
/// </summary>
internal static class TrayProbe
{
    private const string ClassName = "InfiniTranseonSpikeTrayWindow";
    private const uint TrayIconId = 0x1001;
    private const nuint MenuIdPauseAll = 1;
    private const nuint MenuIdToggleOverlay = 2;
    private const nuint MenuIdRecent = 3;
    private const nuint MenuIdExit = 9;

    private static readonly WndProc s_wndProc = WindowProcedure;
    private static FocusSnapshot s_baseline;
    private static int s_menuInteractions;

    internal static int Run()
    {
        Console.WriteLine("probe=tray status=starting durationSeconds=30");
        s_baseline = FocusSnapshot.Take();
        s_baseline.Print("startup-foreground");

        nint hInstance = GetModuleHandle(null);
        var wndClass = new WNDCLASSEX
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            Style = 0,
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            HInstance = hInstance,
            HIcon = LoadIcon(0, IDI_APPLICATION),
            HCursor = LoadCursor(0, IDC_ARROW),
            LpszClassName = ClassName,
        };

        if (RegisterClassEx(ref wndClass) == 0)
        {
            Console.Error.WriteLine($"probe=tray status=error stage=register-class win32={Marshal.GetLastWin32Error()}");
            return ExitCodes.PlatformError;
        }

        // HWND_MESSAGE (-3): a message-only window is invisible, never activates, and cannot
        // steal foreground, yet still receives the tray callback.
        nint hwnd = CreateWindowEx(
            0, ClassName, "InfiniTranseon spike tray host", 0,
            0, 0, 0, 0, unchecked((nint)(-3)), 0, hInstance, 0);
        if (hwnd == 0)
        {
            Console.Error.WriteLine($"probe=tray status=error stage=create-window win32={Marshal.GetLastWin32Error()}");
            _ = UnregisterClass(ClassName, hInstance);
            return ExitCodes.PlatformError;
        }

        var iconData = new NOTIFYICONDATA
        {
            CbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            HWnd = hwnd,
            UID = TrayIconId,
            UFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            UCallbackMessage = WM_TRAYCALLBACK,
            HIcon = LoadIcon(0, IDI_APPLICATION),
            SzTip = "InfiniTranseon platform spike",
            SzInfo = string.Empty,
            SzInfoTitle = string.Empty,
            UVersion = NOTIFYICON_VERSION_4,
        };

        if (!Shell_NotifyIcon(NIM_ADD, ref iconData))
        {
            Console.Error.WriteLine($"probe=tray status=error stage=notifyicon-add win32={Marshal.GetLastWin32Error()}");
            _ = DestroyWindow(hwnd);
            _ = UnregisterClass(ClassName, hInstance);
            return ExitCodes.PlatformError;
        }

        _ = Shell_NotifyIcon(NIM_SETVERSION, ref iconData);
        Console.WriteLine("probe=tray status=icon-added hint=right-click-the-tray-icon-to-exercise-the-native-menu");

        MessageLoop.PumpFor(TimeSpan.FromSeconds(30));

        _ = Shell_NotifyIcon(NIM_DELETE, ref iconData);
        _ = DestroyWindow(hwnd);
        _ = UnregisterClass(ClassName, hInstance);

        Console.WriteLine($"probe=tray status=stopped menuInteractions={s_menuInteractions}");
        return ExitCodes.Success;
    }

    private static nint WindowProcedure(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_TRAYCALLBACK)
        {
            // NOTIFYICON_VERSION_4: the notification event is the low word of lParam.
            uint notification = (uint)(lParam & 0xFFFF);
            if (notification is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                ShowNativeMenu(hWnd);
                return 0;
            }

            return 0;
        }

        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void ShowNativeMenu(nint hWnd)
    {
        s_menuInteractions++;
        int interaction = s_menuInteractions;

        FocusSnapshot before = FocusSnapshot.Take();
        before.Print($"menu-{interaction}-before");
        before.PrintDelta($"menu-{interaction}-before-vs-baseline", s_baseline);

        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            Console.Error.WriteLine($"probe=tray status=error stage=create-menu win32={Marshal.GetLastWin32Error()}");
            return;
        }

        _ = AppendMenu(menu, MF_STRING, MenuIdPauseAll, "Pause all targets");
        _ = AppendMenu(menu, MF_STRING, MenuIdToggleOverlay, "Toggle overlay");
        _ = AppendMenu(menu, MF_STRING, MenuIdRecent, "Recent translations");
        _ = AppendMenu(menu, MF_SEPARATOR, 0, null);
        _ = AppendMenu(menu, MF_STRING, MenuIdExit, "Exit");

        _ = GetCursorPos(out POINT cursor);

        // MSDN-documented dance: the owning window must be foreground for the menu to dismiss
        // on outside click. This deliberately activates the hidden message window; the probe
        // measures exactly how much foreground/focus drift that costs versus the baseline.
        _ = SetForegroundWindow(hWnd);

        FocusSnapshot during = FocusSnapshot.Take();
        during.Print($"menu-{interaction}-during-tracking");

        int selected = TrackPopupMenuEx(
            menu,
            TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_RETURNCMD,
            cursor.X, cursor.Y, hWnd, 0);

        // Required post-menu message so the menu closes correctly on the next click.
        _ = PostMessage(hWnd, WM_NULL, 0, 0);
        _ = DestroyMenu(menu);

        Console.WriteLine($"probe=tray event=menu-selection interaction={interaction} commandId={selected}");

        FocusSnapshot after = FocusSnapshot.Take();
        after.Print($"menu-{interaction}-after");
        after.PrintDelta($"menu-{interaction}-after-vs-baseline", s_baseline);
    }
}
