using InfiniTranseon.PlatformSpike;

// Milestone M0 frontend platform spike. Each probe is an explicit, single-flag command that
// prints structured key=value evidence to stdout, mirroring the backend CaptureSpike pattern.
// Interactive probes (tray/overlay/focus display windows) must only be run in a manual session.
if (args.Length != 1)
{
    PrintUsage();
    return ExitCodes.InvalidUsage;
}

return args[0] switch
{
    "--tray" => TrayProbe.Run(),
    "--overlay" => OverlayProbe.Run(),
    "--pipe" => PipeProbe.Run(),
    "--focus-probe" => FocusProbe.Run(),
    _ => Unknown(args[0]),
};

static int Unknown(string flag)
{
    Console.Error.WriteLine($"error=unknown-flag flag={flag}");
    PrintUsage();
    return ExitCodes.InvalidUsage;
}

static void PrintUsage()
{
    Console.WriteLine("InfiniTranseon.PlatformSpike - Windows 11 frontend platform boundary probes (M0 Task 0)");
    Console.WriteLine();
    Console.WriteLine("Usage: InfiniTranseon.PlatformSpike.exe <command>");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  --tray         Win32 Shell_NotifyIcon tray icon on a hidden message-only HWND with a");
    Console.WriteLine("                 native TrackPopupMenuEx menu; instruments foreground/focus/capture around");
    Console.WriteLine("                 the menu. Interactive: waits ~30s for a right-click, then exits cleanly.");
    Console.WriteLine("  --overlay      Native non-activating click-through layered overlay (WS_EX_NOACTIVATE |");
    Console.WriteLine("                 WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW) via UpdateLayeredWindow,");
    Console.WriteLine("                 with SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE). Interactive: shows ~15s.");
    Console.WriteLine("  --pipe         In-process mock EngineHost named pipe (current-user-only SD); versioned JSON");
    Console.WriteLine("                 handshake with nonce echo, version-mismatch rejection, 100-message latency.");
    Console.WriteLine("  --focus-probe  Opens a Win32 settings window, closes it to tray, then runs the overlay and");
    Console.WriteLine("                 records that the sequence never steals foreground. Interactive.");
    Console.WriteLine();
    Console.WriteLine("Interactive probes display windows and must only be run in an explicit manual test session.");
}
