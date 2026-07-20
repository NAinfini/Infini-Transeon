# Capture platform spike results

**Status:** Harness implemented; manual hardware and consent matrix not yet executed. Backend Tasks 3 and 11 remain blocked.

## Automated evidence

- The package manifest is tested to declare `uap11:Capability Name="graphicsCaptureWithoutBorder"` exactly once.
- The capture spike builds against the Windows 11 SDK and exposes package-identity and borderless-consent probes.
- The borderless-consent command refuses to request access when package identity is absent.
- No automated command opens the system consent prompt.

## Commands

From the configured x64 Debug build output:

```text
InfiniTranseon.CaptureSpike.exe --package-identity
InfiniTranseon.CaptureSpike.exe --request-borderless
```

The second command may display a Windows system prompt and must only be run as part of an explicit manual test session.

## Required manual matrix

| Area | Required cases | Result |
|---|---|---|
| Installer identity | install, update, repair, uninstall as non-admin user | Not run |
| Portable identity | first registration, update, moved directory, revoke, cleanup | Not run |
| Borderless consent | grant, deny, restart persistence, revoke | Not run |
| Capture exclusion | layered, DirectComposition and topmost overlays; window/display capture | Not run |
| Hotkeys | raw-input game, borderless-fullscreen game, windowed game | Not run |
| Adapters and DPI | two adapters, mixed-DPI monitor pair, resize and migration | Not run |

## Exit rule

Do not mark Task 0 or M0 complete until every row has evidence on each supported Windows 11 release. A missing package identity, denied consent, capture-exclusion failure or unavailable hotkey must remain explicit; no test result may be converted into a silent fallback.
