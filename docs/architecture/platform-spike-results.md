# Platform frontend spike results

**Status:** Harness implemented; manual matrix not yet executed. Frontend UI tasks remain gated on the manual session below (plan Task 0). No result row may be marked complete until it has evidence on each supported Windows 11 release.

## Automated evidence

- The spike builds standalone with strict analysis and warnings-as-errors, targeting `net10.0-windows10.0.22621.0`, x64, with no NuGet packages (BCL + Win32 P/Invoke only).
- The no-argument invocation prints usage and exits with code 64 without touching the desktop.
- Every probe is a single explicit flag that emits structured `key=value` evidence to stdout; no probe runs implicitly.
- No automated command has been run against real capture, a real game, Explorer restart, or multiple monitors. Those require the manual session.

## Commands

From the standalone build output (`bin/Debug/net10.0-windows10.0.22621.0/win-x64/`):

```text
InfiniTranseon.PlatformSpike.exe                 # prints usage, exits 64
InfiniTranseon.PlatformSpike.exe --tray
InfiniTranseon.PlatformSpike.exe --overlay
InfiniTranseon.PlatformSpike.exe --pipe
InfiniTranseon.PlatformSpike.exe --focus-probe
```

`--tray`, `--overlay` and `--focus-probe` display windows or a tray icon and interact with the live desktop; `--tray` additionally waits up to 30 seconds for the tester to right-click the icon. These interactive commands must only be run as part of an explicit manual test session, ideally with a real foreground game active so foreground/focus/capture drift is measured against a genuine target. `--pipe` is self-contained and non-interactive but is still listed as a manual-session step so its latency numbers are recorded on representative hardware.

## What each command proves

| Command | Mechanism exercised | Evidence emitted |
|---|---|---|
| `--tray` | `Shell_NotifyIcon` icon owned by a hidden `HWND_MESSAGE` window; native `TrackPopupMenuEx` (`TPM_NONOTIFY \| TPM_RETURNCMD`) on right-click | `snapshot`/`delta` lines for foreground, keyboard focus (`GetGUIThreadInfo`) and mouse capture before, during and after the menu, plus the selected command id |
| `--overlay` | Layered `WS_EX_NOACTIVATE \| WS_EX_TRANSPARENT \| WS_EX_LAYERED \| WS_EX_TOOLWINDOW` window rendered by `UpdateLayeredWindow`; `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` | display-affinity applied/last-error/read-back, `updateLayeredWindow` result, `overlayIsForeground` (must be `False`), and foreground/focus/capture snapshots before/during/after |
| `--pipe` | In-process mock EngineHost `NamedPipeServerStream` with a unique name and `PipeOptions.CurrentUserOnly` (current-user-only security descriptor; .NET named-pipe servers also set `PIPE_REJECT_REMOTE_CLIENTS` by default) | versioned JSON handshake with nonce echo, version-mismatch rejection, and 100-message round-trip latency (`min/p50/mean/p95/p99/max`) |
| `--focus-probe` | Plain Win32 "settings window" shown then hidden to the tray, then the non-activating overlay raised alongside | foreground/focus/capture snapshots at each transition and `overlayIsForeground` (must be `False`), all compared against the tester's pre-probe foreground window |

### Boundary decisions this harness is designed to settle

- Whether a **native** tray menu keeps the game's foreground/focus/capture, so Task 10 can pick native over a XAML flyout with evidence rather than assumption.
- Whether a non-activating layered overlay can be shown, updated and capture-excluded without ever becoming foreground.
- Whether the versioned named-pipe handshake rejects protocol/version mismatch cleanly and what its baseline latency is.
- Whether opening and tray-minimizing the settings window disturbs an unrelated foreground application.

## What still requires a human

The harness cannot self-certify the boundary. A tester must run the interactive commands and observe real behavior for:

- **Tray menu during a real game:** exclusive-fullscreen, borderless-fullscreen and windowed games; confirm the native menu neither minimizes the game nor steals mouse capture, and that `SetForegroundWindow` on the hidden window does not flip the game out of fullscreen.
- **Overlay capture exclusion:** confirm the overlay is absent from both window capture and full-display capture, and that it stays click-through (input passes to the game).
- **Explorer restart recovery:** kill and restart `explorer.exe` and confirm the tray icon re-registers (or that the re-add path is implemented in the real app).
- **EngineHost restart:** drop and restart the mock server and confirm the client re-handshakes without a fake-success path.
- **DPI variations:** 100%, 150%, 200% and 300%; confirm overlay position/size and text rendering, and menu placement.
- **Multi-monitor:** mixed-DPI monitor pair, overlay on the non-primary monitor, and menu anchoring on the monitor that owns the cursor.
- **Windows 11 releases:** repeat on each supported release (22H2 baseline and later).

## Required manual matrix

| Area | Required cases | Result |
|---|---|---|
| Tray icon lifecycle | add, right-click menu, dismiss, delete; Explorer restart re-registration | Not run |
| Tray menu focus | foreground/focus/capture unchanged for a real game (exclusive, borderless, windowed) | Not run |
| Overlay non-activation | `overlayIsForeground=False`; foreground/focus/capture unchanged before/during/after | Not run |
| Overlay capture exclusion | omitted from window capture and display capture; input passes through | Not run |
| Overlay rendering | `UpdateLayeredWindow` legible at 100/150/200/300% DPI, primary and secondary monitor | Not run |
| Pipe handshake | good handshake accepted with nonce echo; version 999 rejected | Not run |
| Pipe latency | 100-message min/p50/mean/p95/p99/max on representative hardware | Not run |
| Settings-to-tray focus | opening and tray-minimizing never steals foreground from the tester's active window | Not run |
| Windows 11 releases | repeat every row on each supported release | Not run |

## Exit rule

Do not mark plan Task 0 or milestone M0 complete, and do not unblock the later frontend UI tasks, until every row above has evidence on each supported Windows 11 release. A tray menu that activates the game, an overlay that becomes foreground or appears in capture, a handshake that accepts a mismatched version, or a settings window that steals focus must remain an explicit failure; no result may be converted into a silent fallback.
