# Hardware acceptance — 2026-07-23

This record separates observed evidence from work that still needs a human-operated game,
package identity, or cloud credential. A missing prerequisite is never converted into a pass.

## Host

- Windows 11 25H2, build `26200.8875`, x64.
- Primary display: `3840x2160 @ 160 Hz`, 175% coordinate scale, window DPI 168.
- Secondary display: `2560x1440 @ 144 Hz`, 125% coordinate scale, window DPI 120.
- DXGI inventory: NVIDIA GeForce RTX 4080, AMD Radeon Graphics, and the Microsoft Basic
  Render Driver. Both attached outputs are on the first RTX 4080 adapter.
- Twelve visible top-level capture candidates were enumerated across both displays. No real
  game window was open during this session.
- No `InfiniTranseon/Provider/*` entries existed in Windows Credential Manager.

## Automated acceptance

| Check | Result | Evidence |
|---|---|---|
| Managed build | Pass | `dotnet build InfiniTranseon.sln -c Debug --no-restore`: 0 warnings, 0 errors |
| Managed tests | Pass | Latest Release run: 886 passed, 0 failed, 0 skipped |
| Native build | Pass | `cmake --build --preset windows-x64-debug` |
| Native tests | Pass | 13/13 passed |
| Runtime hot apply | Pass (automated) | Coordinator replaces processing and translation configuration without restarting capture; workbench persists every target and region |
| Thumbnail protocol | Pass (automated) | Managed codec and native parser cover valid and invalid bounds; native capture path limits readback and PNG output |

## Live desktop probes

| Check | Result | Evidence and limits |
|---|---|---|
| Named-pipe handshake | Pass | Correct version accepted with nonce echo; version 999 rejected |
| Named-pipe latency | Pass | 100 messages: min 0.032 ms, p50 0.075 ms, mean 0.082 ms, p95 0.142 ms, p99 0.215 ms, max 0.260 ms |
| Overlay non-activation | Pass | Foreground, keyboard focus and mouse capture unchanged before/during/after; `overlayIsForeground=False` |
| Overlay display affinity API | Pass | `WDA_EXCLUDEFROMCAPTURE` applied and read back as `0x11` |
| Settings-to-tray focus | Pass | Test settings window, tray transition and overlay did not change the foreground, focus or capture state |
| Capture exclusion pixels | Manual observation still required | API passed, but this run did not independently record both a window-capture and display-capture image |
| Mixed-DPI inventory | Pass | Live windows reported DPI 168 on display 1 and DPI 120 on display 2 |
| Package identity | Not applicable (release decision) | The unsigned GitHub distribution intentionally ships without package identity. The app must start without registration or UAC, retain the Windows capture border, and record `capture.borderless.unavailableWithoutPackageIdentity` instead of silently degrading |

The unsigned namespace rules are documented by Microsoft:
<https://learn.microsoft.com/windows/msix/package/unsigned-package>.
Microsoft explicitly positions unsigned MSIX as a testing mechanism rather than a broad
distribution mechanism. The installer can perform an elevated registration, but an unsigned
portable first launch cannot promise a silent, non-admin identity registration.

## Required game and service session

| Area | Status | Exact prerequisite and acceptance evidence |
|---|---|---|
| Windowed real game | Not run | Open a game and bind at least two regions; prove preview, OCR, translation, overlay alignment, resize tracking and hot apply |
| Borderless-fullscreen game | Not run | Use the unsigned identity-free build; prove capture and overlay alignment with the Windows capture border retained, no focus loss, and resize/recreate recovery. Confirm the downgrade is visible and logged |
| Mixed DPI migration | Partially run | Inventory passed; move a running game and overlay between the 175% and 125% displays and record region alignment before/after |
| Multi-window capture | Not run | Open two eligible target windows simultaneously; prove both remain live, independently pausable and bounded by configured performance budgets |
| Cloud translation/OCR | Not run | Add credentials in the app; test at least one US/global translation service, one China translation service and one cloud OCR service without exposing secrets |
| Border downgrade persistence | Not run | Restart the unsigned build and verify package identity is still absent, capture remains usable with the system border, and the explicit downgrade status is shown and logged again |

## Exit rule

The workbench and hot-apply implementation may be treated as code-complete, but the product is not
hardware-release-approved until every `Not run`, `Partially run`, and manual-observation row above
has evidence from a real game session. A row explicitly marked not applicable by the unsigned
distribution decision is not a failure. The Windows 11 support matrix must then repeat the session
on each supported release.

## 2026-07-24 follow-up

- A clean Release build outside OneDrive (`C:\tmp\InfiniTranseon-native-verify-20260724`) completed
  and all 13 native tests passed. This proves the earlier ABI/coordinate test launch failures were
  caused by the repository path execution policy rather than failed assertions.
- The native EngineHost and CaptureSpike previously had no embedded DPI-awareness manifest.
  CaptureSpike consequently reported `96x96` through `GetDpiForMonitor`, even though live windows
  correctly reported DPI 168 and 120. Both native executables now merge a Per-Monitor-V2 manifest,
  and the release workflow extracts the final PE manifest with the Windows SDK before packaging.
- The corrected live display probe reports primary `3840x2160`, DPI `168`, scale `175%`; secondary
  `2560x1440`, DPI `120`, scale `125%`, including the negative secondary-monitor origin.
- The live window inventory found Counter-Strike 2 as a borderless-sized candidate on the primary
  display. No capture, overlay injection, focus manipulation, UAC prompt, or package registration
  was performed while that user-owned game session was active.
- A subsequent packaged-app launch exposed a release blocker before WinUI startup: the
  organization-ID publisher used by the unsigned identity package is rejected by the Win32
  activation-context parser. A plain certificate subject loads, but Microsoft requires an
  external-location identity package on end-user machines to carry a trusted signature. The
  unsigned release path now omits that package, starts without UAC, records
  `capture.borderless.unavailableWithoutPackageIdentity`, and retains the system capture border.
- The first identity-free launch then exposed a second release blocker: the unpackaged publish
  omitted the app's `resources.pri`, and every parameterless MRT Core `ResourceLoader` assumed a
  package default resource map. The project now generates an unpackaged `Application` PRI during
  build/publish, requires it in MSI and portable layouts, and loads the `Resources` subtree from
  the explicit default PRI path. A live launch remained responsive, created a visible
  `WinUIDesktopWin32WindowClass` titled `Infini-Transeon`, rendered the Chinese profile screen, and
  wrote the expected borderless downgrade status. The full managed suite passed 667/667.
- After the manifest rebuild, this Codex environment rewrote only the new EngineHost file ACL from
  inherited execute permissions to `Everyone:(R), Administrators:(F)`. The post-change CTest run
  therefore recorded 12/13 with the ABI CLI probe not started; the other 12 tests passed. The ACL
  was not weakened or bypassed. GitHub Actions and a clean non-sandbox Win11 machine must run the
  final packaged EngineHost.

## 2026-07-28 `0.1.0` release-candidate follow-up

- The complete managed Release suite passed 886/886 with no failures or skips.
- A fresh local-model-enabled native Release build completed in
  `artifacts/cmake/release-candidate`. Twelve of thirteen CTest processes passed; the remaining ABI
  executable was not started because this Codex/OneDrive path removed execute permission. Its
  assertions did not run and are not recorded as a pass. The prior clean `C:\tmp` run remains the
  13/13 evidence, and the tagged GitHub Actions run is required before publication.
- The deterministic OCR benchmark contract smoke passed two synthetic samples with CER 0, line
  accuracy 1, and local P95 48 ms. This proves the report and threshold plumbing, not real-model
  OCR quality.
- The unsigned MSI and portable archive were built locally and checked for required files, native
  dependencies, Per-Monitor-V2 EngineHost manifest, empty model payload, SBOM/license allowlist,
  Authenticode `NotSigned` status, and Ed25519 model-catalog signature. All nine immutable
  model-catalog URLs returned HTTP 200 with their declared byte sizes.
- These automated results do not change the manual rows above. Real-game windowed and borderless
  capture, mixed-DPI movement, simultaneous multi-window use, capture-exclusion pixels, and
  credentialed US/China translation plus cloud OCR remain release-owner acceptance decisions.
