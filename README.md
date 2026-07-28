# Infini-Transeon

Infini-Transeon is an open-source Windows game-translation overlay. It captures a
window, monitor, or fixed desktop area; recognizes visible text; translates it
through services or local models selected by the user; and renders the result
over or near the original text.

> [!WARNING]
> The current MSI and application binaries are not Authenticode-signed. Windows
> shows **Unknown publisher**, and SmartScreen may warn. Download builds only
> from this repository's [GitHub Releases](https://github.com/NAinfini/Infini-Transeon/releases).
> The in-app updater verifies an Ed25519-signed manifest, file size, and SHA-256
> hash before offering an installer.

## Requirements

- Windows 11 x64, build 22621 (22H2) or newer
- A windowed or borderless-fullscreen game; exclusive fullscreen is not supported
- User-supplied credentials for online OCR, translation, or LLM services
- Optional local models are downloaded only after explicit confirmation

Windows 10 and unsupported Windows 11 releases are rejected explicitly rather
than silently degraded.

## Highlights

- Multiple simultaneous windows, monitors, and fixed desktop regions
- Per-game profiles with multiple named, normalized capture regions
- Full-window automatic scanning alongside user-defined high-priority regions
- Per-region OCR cadence, priority, line-break behavior, translation groups, and
  one to four stable result slots
- Parallel translators, direct contextual LLM translation, and optional LLM
  refinement
- Built-in global and China-accessible translation services, OpenAI-compatible
  endpoints, and configurable REST adapters
- In-place replacement, translucent or blurred backing, offset text, and hover
  panel overlay modes
- Light, dark, high-contrast, English, and Simplified Chinese interfaces
- Global hotkeys and a notification-area menu designed to avoid leaving the game
- Versioned profile import/export without API keys, history, screenshots, models,
  or personal paths
- Optional per-profile history with time and storage limits
- Explicit performance degradation, diagnostics, and per-region degradation locks

## Install and start

1. Download `Infini-Transeon.msi` or `Infini-Transeon-portable.zip` from
   [Releases](https://github.com/NAinfini/Infini-Transeon/releases).
2. Accept the unsigned-publisher warning only if the download came from this
   repository.
3. Add at least one translation service and its credential.
4. Create a profile, select one or more capture targets, and define regions or
   enable full-window scanning.
5. Configure translation channels and an overlay strategy for each region.
6. Start the profile. Use global hotkeys or the tray menu while playing.

The portable build stores user data under `%LOCALAPPDATA%\InfiniTranseon`; it
does not place credentials or history beside the executable.

## Privacy and cost

Cloud OCR and translation send configured text or image crops to the providers
the user selects. Strict offline mode blocks those requests. Infini-Transeon
does not operate a crash-log collection server, and local models are neither
bundled nor downloaded automatically. Diagnostics record application state and
errors, not OCR text or translation content.

## Build from source

The repository requires the .NET 10 SDK, CMake, Visual Studio 2026 with the
Desktop development with C++ workload, and a Windows 11 SDK.

```powershell
dotnet tool restore
dotnet restore InfiniTranseon.sln
dotnet test InfiniTranseon.sln -c Release --no-restore
cmake --preset windows-x64 -DINFINI_ENABLE_LOCAL_MODEL_RUNTIME=ON
cmake --build --preset windows-x64-release
ctest --test-dir artifacts/cmake/windows-x64 -C Release --output-on-failure
```

Release packaging and unsigned-build details are documented in
[docs/release/github-release.md](docs/release/github-release.md). Hardware
acceptance evidence and outstanding manual checks are recorded in
[docs/testing/hardware-acceptance-2026-07-23.md](docs/testing/hardware-acceptance-2026-07-23.md).

## License

Licensed under the [Apache License 2.0](LICENSE). Third-party notices are
published with every release.
