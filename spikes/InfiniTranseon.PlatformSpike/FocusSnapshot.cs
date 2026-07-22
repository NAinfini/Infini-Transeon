using System.Runtime.InteropServices;
using static InfiniTranseon.PlatformSpike.NativeMethods;

namespace InfiniTranseon.PlatformSpike;

/// <summary>
/// Immutable capture of the three input-ownership HWNDs the frontend must never disturb:
/// the system foreground window, and the keyboard-focus / mouse-capture windows owned by
/// the foreground GUI thread (read via GetGUIThreadInfo(0, ...)).
/// </summary>
internal readonly record struct FocusSnapshot(
    nint Foreground,
    nint Focus,
    nint Capture,
    nint Active,
    nint MenuOwner,
    string ForegroundTitle,
    uint ForegroundPid)
{
    internal static FocusSnapshot Take()
    {
        nint foreground = GetForegroundWindow();

        var gti = new GUITHREADINFO { CbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        // idThread 0 => the foreground thread, so focus/capture reflect the active app,
        // not this spike process (GetCapture/GetFocus are queue-local and would read blank).
        _ = GetGUIThreadInfo(0, ref gti);

        _ = GetWindowThreadProcessId(foreground, out uint pid);
        return new FocusSnapshot(
            foreground,
            gti.HwndFocus,
            gti.HwndCapture,
            gti.HwndActive,
            gti.HwndMenuOwner,
            WindowTitle(foreground),
            pid);
    }

    private static string WindowTitle(nint hWnd)
    {
        if (hWnd == 0)
        {
            return "<none>";
        }

        char[] buffer = new char[256];
        int length = GetWindowText(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "<untitled>";
    }

    /// <summary>Emits a single structured key=value evidence line to stdout.</summary>
    internal void Print(string label)
    {
        Console.WriteLine(
            $"snapshot label={label} " +
            $"foreground=0x{Foreground:X} " +
            $"focus=0x{Focus:X} " +
            $"capture=0x{Capture:X} " +
            $"active=0x{Active:X} " +
            $"menuOwner=0x{MenuOwner:X} " +
            $"foregroundPid={ForegroundPid} " +
            $"foregroundTitle=\"{ForegroundTitle}\"");
    }

    /// <summary>Reports whether input ownership drifted relative to a reference snapshot.</summary>
    internal void PrintDelta(string label, FocusSnapshot reference)
    {
        bool foregroundChanged = Foreground != reference.Foreground;
        bool focusChanged = Focus != reference.Focus;
        bool captureChanged = Capture != reference.Capture;
        Console.WriteLine(
            $"delta label={label} " +
            $"foregroundChanged={foregroundChanged} " +
            $"focusChanged={focusChanged} " +
            $"captureChanged={captureChanged}");
    }
}
