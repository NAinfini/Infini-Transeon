# Capture platform spike results

**Status:** Harness implemented; one local desktop session was partially executed on 2026-07-23.
See `docs/testing/hardware-acceptance-2026-07-23.md`. Real-game, consent-persistence,
multi-window runtime and cloud-service rows remain open.

## Automated evidence

- The package manifest is tested to declare `uap11:Capability Name="graphicsCaptureWithoutBorder"` exactly once.
- The capture spike builds with strict MSVC warnings and exposes package-identity, borderless-consent,
  capture-exclusion, global-hotkey and DXGI adapter/output probes.
- The borderless-consent command refuses to request access when package identity is absent.
- The adapter inventory command completed on the development host and reported all DXGI adapters plus
  attached-output physical coordinates. This is inventory evidence only, not cross-adapter capture evidence.
- The display inventory command reports current display modes plus coordinate scale; the window inventory
  command reports UTF-8 top-level capture candidates with window DPI and owning monitor.
- No automated command opens the system consent prompt.

## Commands

From the configured x64 Debug build output:

```text
InfiniTranseon.CaptureSpike.exe --package-identity
InfiniTranseon.CaptureSpike.exe --request-borderless
InfiniTranseon.CaptureSpike.exe --capture-exclusion
InfiniTranseon.CaptureSpike.exe --hotkey-probe
InfiniTranseon.CaptureSpike.exe --adapter-inventory
InfiniTranseon.CaptureSpike.exe --display-inventory
InfiniTranseon.CaptureSpike.exe --window-inventory
```

The borderless command may display a Windows system prompt. The exclusion command verifies the display-affinity
API and shows a probe window for 15 seconds, but a tester must still confirm that both window and display capture
omit it. The hotkey command waits up to 30 seconds for `Ctrl+Alt+F10`. These interactive commands must only be run
as part of an explicit manual test session.

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
