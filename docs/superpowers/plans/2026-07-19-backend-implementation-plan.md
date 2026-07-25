# Infini-Transeon Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a low-latency, multi-target Windows OCR and translation runtime that preserves game performance, renders stable click-through overlays, supports user-supplied online services and optional local models, and never hides failures or privacy-impacting behavior.

**Architecture:** The .NET 10 App owns profiles, SQLite, translation orchestration, online OCR/translation/LLM adapters, history, updates and diagnostics. A supervised `EngineHost.exe` loads the C++20 engine for Windows capture, D3D11 image flow, local OCR, target tracking and native overlays; an optional restricted `ModelWorker.exe` runs user-requested local translation models. Normal operation uses two resident processes. Versioned secured named pipes connect App and EngineHost, while a versioned C ABI remains internal to EngineHost and keeps complete frames outside App. The authoritative ownership, data-flow, data-structure, Big-O and performance rules are in `docs/superpowers/specs/2026-07-19-runtime-architecture-design.md`.

**Tech Stack:** C# 14, .NET 10 LTS, C++20, Windows App SDK 2.2 stable, Windows.Graphics.Capture, Win32, D3D11, DirectComposition, Direct2D, DirectWrite, ONNX Runtime/Windows ML CPU baseline, SQLite, Windows Credential Manager, CMake, MSBuild, xUnit, GoogleTest, WiX or signed bootstrapper, GitHub Releases.

## Global Constraints

- Target Windows 11 x64 only, with API baseline build 22621 (22H2); Windows 10 is not supported (see `docs/adr/2026-07-20-adr-002-windows-11-minimum.md`) and the installer must refuse unsupported Windows versions with a clear message. The release test matrix covers currently Microsoft-supported Windows 11 releases. Support windowed and borderless fullscreen, not guaranteed exclusive fullscreen.
- Support any capturable window plus display and desktop-fixed-region targets; support multiple simultaneous targets in the first public release.
- Never inject into games, hook game text, read game memory, or interact with anti-cheat systems.
- Never run full OCR over a complete 4K frame on a periodic timer; use low-resolution detection and original-resolution crops.
- Every queue is bounded and generation-aware; stale OCR, translation, refinement, and overlay results are discarded.
- A region may run 1–4 translation channels for the same source line; each channel has at most 2 fallbacks, 1 retry per provider attempt and 2 refinement steps. Results are independent and automatically rendered in fixed slots.
- Do not implement LLM candidate judging. LLMs may perform initial translation or explicit finite refinement only.
- Do not download local OCR/translation acceleration packs or models without a direct user request.
- Strict offline mode constructs no provider or update HTTP clients, checks no updates, performs no provider health checks, and emits no network traffic.
- Cloud OCR is opt-in and receives only the configured crop; ordinary online translation receives recognized text, not screenshots.
- Logs contain application state but no OCR text or translated text; secrets never enter files, databases, logs, command lines or exports. Default crash records contain structured metadata only; memory-bearing dumps require separate explicit consent and privacy warnings.
- All adaptive degradation is visible and logged. A user-locked region is never automatically reconfigured; if capacity cannot satisfy it, old work is discarded and the region enters an explicit paused/error state.
- Built-in adapters, OpenAI-compatible endpoints, and declarative REST adapters are allowed; executable code plugins are not.
- Publish free and open source under Apache-2.0 with installer and portable artifacts on GitHub Releases; check updates by default, install only after user confirmation and signature verification.

---

## Planned file structure

```text
InfiniTranseon.sln
CMakePresets.json
Directory.Build.props
Directory.Packages.props
cmake/
src/InfiniTranseon.Contracts/
src/InfiniTranseon.Core/
  Profiles/
  Scheduling/
  Ocr/
  Translation/
  Storage/
  Diagnostics/
  Privacy/
  Updates/
src/InfiniTranseon.Engine.Native/
  include/infini_engine.h
  src/abi/
  src/capture/
  src/imaging/
  src/ocr/
  src/scheduling/
  src/overlay/
  src/diagnostics/
src/InfiniTranseon.EngineHost/
  main.cpp
  ipc/
  supervision/
src/InfiniTranseon.ModelWorker/
src/InfiniTranseon.App/
tests/InfiniTranseon.Core.Tests/
tests/InfiniTranseon.Engine.Tests/
tests/InfiniTranseon.IntegrationTests/
benchmarks/GameOcrBench/
packaging/
  release-manifest.schema.json
LICENSE
NOTICE
```

## Core contracts

```csharp
public sealed record CaptureTargetId(Guid Value);
public sealed record TargetInstanceId(Guid Value);
public sealed record RegionId(Guid Value);
public sealed record TextTrackId(Guid Value);
public sealed record SourceEventId(Guid Value);
public sealed record TranslationChannelId(Guid Value);

public sealed record NormalizedRect(double X, double Y, double Width, double Height);

public sealed record SourceGenerationToken(
    Guid RuntimeEpoch,
    TargetInstanceId TargetInstanceId,
    CaptureAreaKey Area,
    TextTrackId TextTrackId,
    long SourceGeneration,
    long ProfileRevision);

public enum CaptureAreaKind { UserRegion, FullTarget, RemainingArea }
public sealed record CaptureAreaKey(CaptureAreaKind Kind, RegionId? UserRegionId);

public sealed record OcrExecutionToken(
    SourceGenerationToken Source,
    Guid OcrRunId,
    int Attempt,
    long ResultSequence);

public sealed record ChannelExecutionToken(
    SourceGenerationToken Source,
    TranslationChannelId ChannelId,
    Guid ChannelRunId,
    Guid ImmutableSlotId);

public sealed record StageExecutionToken(
    ChannelExecutionToken Channel,
    Guid StageId,
    int StageSequence,
    int Attempt,
    long StreamSequence);

public sealed record TextGeneration(
    CaptureTargetId TargetId,
    SourceGenerationToken SourceToken,
    SourceEventId SourceEventId,
    NormalizedRect SourceBounds,
    string SourceText,
    IReadOnlyList<TextLine> Lines,
    DateTimeOffset CapturedAtUtc,
    long CapturedAtQpc,
    long FrameSequence);

public sealed record TranslationChannelDefinition(
    TranslationChannelId Id,
    string InitialProviderId,
    IReadOnlyList<string> FallbackProviderIds,
    IReadOnlyList<RefinementStepDefinition> RefinementSteps,
    ContextPolicy Context,
    CachePolicy Cache,
    DisplaySlotDefinition DisplaySlot);

public sealed record TranslationOutput(
    TranslationChannelId ChannelId,
    StageExecutionToken ExecutionToken,
    Guid ImmutableSlotId,
    Guid StageId,
    int StageIndex,
    int Attempt,
    TranslationStage Stage,
    string Text,
    string ProviderId,
    TimeSpan Latency,
    decimal? EstimatedCost,
    string? CostCurrency,
    bool CacheHit,
    bool StreamCompleted,
    string? FallbackFromProviderId,
    string? TerminalErrorCode,
    string? SupersededReason);
```

The native ABI uses opaque handles, explicit structure sizes and ABI version fields. Managed exceptions never cross the ABI; native failures return stable error codes, localization keys and typed arguments. Every IPC/ABI contract defines UTF-8 encoding, allocator, buffer owner/free function, callback thread, non-reentrancy, cancellation, maximum message size, ordering and the guarantee that no callback occurs after destroy acknowledgement.

## Task 0: Prove capture border, capture exclusion, and hotkey platform boundaries

**Files:**
- Create: `spikes/InfiniTranseon.CaptureSpike/`
- Create: `docs/architecture/capture-spike-results.md`
- Create: `packaging/identity/Package.appxmanifest`
- Create: `packaging/identity/README.md`

**Interfaces:**
- Proves: WGC capture-border behavior per supported OS build, `WDA_EXCLUDEFROMCAPTURE` reliability without an OCR feedback loop, global hotkey delivery under raw-input games, and cross-adapter/mixed-DPI capture behavior.

- [ ] Verify system capture-border removal via `GraphicsCaptureSession.IsBorderRequired = false` on every supported Windows 11 release, including the `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` user-consent flow: consent granted, consent denied, and persistence across app restarts. Declare `graphicsCaptureWithoutBorder` in the package manifest and prove that both installer and portable artifacts obtain package identity before requesting access. Record the honest disclosure behavior for the consent-denied case (border stays visible); write the outcome back into the immersion contract in `docs/product/2026-07-19-product-ux-architecture-review.md`.
- [ ] Treat portable package identity as a release-blocking spike result: test the selected external-location/sparse-package registration and removal lifecycle from an ordinary non-admin user account, including first run, upgrade, moved portable directory, revoked registration and uninstall/cleanup. If the portable artifact cannot acquire the capability reliably, stop and revise the portable distribution contract; do not silently keep the capture border or claim borderless support.
- [ ] Verify `WDA_EXCLUDEFROMCAPTURE` on layered, DirectComposition and topmost overlay windows against both window and display WGC capture; prove no overlay feedback loop forms, and prove the failure path disables the affected overlay with an explicit error.
- [ ] Verify global hotkey delivery against at least one raw-input game, one borderless-fullscreen game and one windowed game; record per-mode reliability and confirm the UI can truthfully report an unavailable hotkey instead of silently failing.
- [ ] Capture two windows on different adapters plus a mixed-DPI monitor pair; record adapter LUID, DPI and frame-arrival behavior EngineHost must handle.
- [ ] Block backend Tasks 3 and 11 until results are recorded or the runtime architecture spec is revised to match reality.
- [ ] Commit with `spike(engine): validate capture border exclusion and hotkey boundaries`.

## Task 1: Establish solution, build system, contracts, and architecture tests

**Files:**
- Create: `InfiniTranseon.sln`
- Create: `CMakePresets.json`
- Create: `src/InfiniTranseon.Core/InfiniTranseon.Core.csproj`
- Create: `src/InfiniTranseon.Engine.Native/CMakeLists.txt`
- Create: `src/InfiniTranseon.Engine.Native/include/infini_engine.h`
- Create: `src/InfiniTranseon.EngineHost/CMakeLists.txt`
- Create: `src/InfiniTranseon.EngineHost/ipc/runtime-protocol.json`
- Create: `src/InfiniTranseon.Contracts/Runtime/RuntimeProtocol.cs`
- Create: `src/InfiniTranseon.Contracts/Runtime/RuntimeContracts.cs`
- Create: `src/InfiniTranseon.Contracts/Runtime/RuntimeCapabilities.cs`
- Create: `src/InfiniTranseon.Contracts/Translation/DeclarativeRestAdapterDefinition.cs`
- Create: `tests/InfiniTranseon.Testing/Fakes/FakeCaptureProbe.cs`
- Create: `tests/InfiniTranseon.Testing/InfiniTranseon.Testing.csproj`
- Create: `tests/InfiniTranseon.Testing/Fakes/FakeOcrProbe.cs`
- Create: `tests/InfiniTranseon.Testing/Fakes/FakeTranslationProbe.cs`
- Create: `tests/InfiniTranseon.Testing/Fakes/FakeOverlayPreviewRenderer.cs`
- Create: `tests/InfiniTranseon.Testing/Fixtures/probe-golden-fixtures.json`
- Create: `packaging/release-manifest.schema.json`
- Create: `packaging/model-catalog.schema.json`
- Create: `LICENSE`
- Create: `NOTICE`
- Test: `tests/InfiniTranseon.Core.Tests/Architecture/DependencyTests.cs`
- Test: `tests/InfiniTranseon.Engine.Tests/abi_contract_tests.cpp`

**Interfaces:**
- Produces: versioned `IT_EngineApi`, opaque `IT_EngineHandle`, complete ABI/IPC envelopes, named-pipe handshake, ownership table, diagnostic localization contract, release trust-root protocol and the managed contracts listed above.
- Constraint: `Core` cannot depend on WinUI; `Engine.Native` cannot depend on managed assemblies.

- [ ] Configure x64 Debug/Release builds, warnings-as-errors, sanitizers for native test builds, deterministic managed builds, and centralized dependency versions.
- [ ] Keep packaging identity separate from product code: the installer and portable bootstrap paths may establish identity, but Core, Contracts and Engine.Native must not depend on deployment format. Add a build-time check that the manifest declares `graphicsCaptureWithoutBorder` exactly once.
- [ ] Define all bidirectional flows before feature work: control, target lifecycle, OCR result, cloud-OCR crop request, translation output/stream chunk, overlay update, source/channel/stage execution-token hierarchy, policy revision/acknowledgement, degradation snapshot, diagnostics and thumbnail. Specify encoding, allocation/free, callback threads, cancellation, backpressure, ordering and reconnect snapshots.
- [ ] Implement a secured named-pipe envelope with protocol/version handshake, current-logon-SID ACL, remote-client rejection, random session name, first-instance protection, length limits, request IDs and bounded queues. Bind each handshake to expected PID, runtime epoch and one-time nonce/inherited bootstrap handle. App supervises EngineHost and the optional ModelWorker with Job Objects and finite restart policies.
- [ ] Add an architecture test that accepts only App plus EngineHost as resident processes in normal online/offline operation and permits ModelWorker only while a user-enabled local model is active; reject any accidental provider or updater process project.
- [ ] Define `RuntimeCapabilities v1` with the exact safety ceilings from the runtime architecture spec plus a dynamic `RuntimeBudgetSnapshot` reporting per-pool limit, committed, reserved and available bytes/slots. Validate the same contract in profile import, App editing, EngineHost admission and reconnect; preserve over-limit imported items as disabled with an explicit reason instead of truncating them.
- [ ] Define canonical signed release manifest fields, embedded Ed25519 trust root, dual-key rotation, anti-downgrade state and an explicit per-artifact code-signing policy before updater work begins; current Windows releases declare `unsigned`, while future Authenticode releases bind the exact publisher identity.
- [ ] Define a separate signed model-catalog schema and embedded public-key set binding model ID/version, every file hash/size, license, runtime/opset, architecture and download origin; specify dual-key rotation and catalog anti-rollback.
- [ ] Define `ICaptureProbe`, `IOcrProbe`, `ITranslationProbe` and `IOverlayPreviewRenderer` contracts plus deterministic fakes/golden fixtures in Task 1 so frontend work never invents incompatible probe data. Real adapters replace fakes only after backend Tasks 3, 6, 8 and 11 respectively.
- [ ] Add `InfiniTranseon.Testing.csproj` to the solution as a presentation-neutral shared test-support project referencing Contracts only; Core, Engine and production App projects may not reference it.
- [ ] Add Apache-2.0 LICENSE/NOTICE placeholders containing actual project identity, plus CI contracts for SBOM and incompatible dependency/model-license rejection.
- [ ] Add architecture, ABI and IPC golden-contract tests that reject mismatched versions, invalid ownership and out-of-order acknowledgements without crashing.
- [ ] Run `dotnet test tests/InfiniTranseon.Core.Tests -c Debug` and `ctest --preset windows-x64-debug`; expect all tests to pass.
- [ ] Commit with `build: establish managed-native solution boundaries`.

## Task 2: Define versioned profiles, validation, import/export, and migrations

**Files:**
- Create: `src/InfiniTranseon.Core/Profiles/ProfileDocument.cs`
- Create: `src/InfiniTranseon.Core/Profiles/ProfileValidator.cs`
- Create: `src/InfiniTranseon.Core/Profiles/ProfileMigrator.cs`
- Create: `src/InfiniTranseon.Core/Profiles/ProfileArchiveService.cs`
- Create: `src/InfiniTranseon.Core/Settings/ApplicationSettings.cs`
- Create: `src/InfiniTranseon.Core/Settings/ApplicationSettingsRepository.cs`
- Create: `src/InfiniTranseon.Core/Storage/DatabaseMigrator.cs`
- Test: `tests/InfiniTranseon.Core.Tests/Profiles/ProfileTests.cs`

**Interfaces:**
- Produces: SQLite as the single authoritative profile/settings store, `ProfileDocument CurrentVersion`, versioned `ApplicationSettings{UiLanguage, FormattingRegionMode, FormattingRegion}`, `ValidationResult`, `SanitizedProfileArchive`, transactional schema journal and backup/restore result.
- Includes: targets, normalized regions, layout variants, OCR settings, translation channels, context, overlay, line breaks, priorities, schedules, degradation locks, history retention, and hotkeys.

- [ ] Create SQLite connection, journal-mode capability detection, schema journal, backup/restore and atomic migration infrastructure before any profile save. App is the only database owner; portable binaries still default active data to per-user local storage.
- [ ] Define JSON schema with integer schema version, stable GUIDs, explicit defaults, and extension-data preservation for forward-safe round trips.
- [ ] Store application language and formatting region in global `ApplicationSettings`, never inside a game profile. Use invariant culture and explicit UTF-8 for IPC, cache keys, JSON and SQLite numeric serialization regardless of current UI culture.
- [ ] Validate normalized geometry, unique IDs, 1–4 channels, at most 2 fallbacks/2 refinements/1 retry per attempt, finite acyclic stages, provider budgets, at least one enabled channel when translation is enabled, and compatible offline policies.
- [ ] Implement atomic SQLite save and one-way migrations with rollback and backup recovery on failure; reject or switch away from WAL on network/removable/unsupported file systems.
- [ ] Export a versioned archive that excludes secrets, history, screenshots, models, personal paths, native handles, logs, and machine-specific target bindings.
- [ ] Test corrupted documents, future versions, partial migration failure, locale-independent numbers, duplicate IDs, invalid REST references, and export redaction.
- [ ] Commit with `feat(core): add safe versioned profile documents`.

## Task 3: Implement Windows capture and target lifecycle tracking

**Files:**
- Create: `src/InfiniTranseon.Engine.Native/src/capture/capture_session.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/capture/capture_source_runtime.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/capture/window_target.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/capture/display_target.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/capture/desktop_region_target.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/capture/target_tracker.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/capture/frame_lease.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/imaging/device_runtime.cpp`
- Test: `tests/InfiniTranseon.Engine.Tests/capture_session_tests.cpp`
- Test: `tests/InfiniTranseon.IntegrationTests/Capture/CaptureLifecycleTests.cs`

**Interfaces:**
- Produces: GPU-resident `CapturedFrame{captureSourceKey, frameSequence, qpcTimestamp, contentSize, dpi, adapterLuid, deviceEpoch, ID3D11Texture2D*}` plus logical target ROI references.
- Emits lifecycle: available, running, minimized, occluded/unsupported, resized, DPI changed, closed, waiting-for-match.

- [ ] Create one `CaptureSourceRuntime` per HWND or `(HMONITOR, AdapterLuid)`. Window, display and desktop-fixed logical targets reference a shared physical source and independent ROI/policy state. Split cross-display desktop-fixed regions into per-monitor physical-pixel sub-ROIs and composite their OCR/layout results; never duplicate a full-display WGC pool for multiple ROIs.
- [ ] Exclude Infini-Transeon overlays from capture and verify exclusion with an automated marker test.
- [ ] Track client-area origin, resize, monitor transition, adapter LUID, device epoch, DPI, minimization, cloaking/virtual desktop, foreground/z-order, closure and recreation without binding to a merely similar window title.
- [ ] Implement a root `FrameLease` plus one `GpuUseTicket` per logical-target consumer. Latest-slot replacement releases only the root slot reference; cancellation releases an unsubmitted ticket, and only its fence completion releases a submitted ticket. Close `Direct3D11CaptureFrame` only after slot and all tickets reach zero. Detection binds `CaptureSourceKey + FrameSequence + DeviceEpoch + FrameLeaseId`; recognition uses a bounded immutable `CropLease` copied from that exact frame. A `ComPtr` reference increment is not a copy.
- [ ] Create one `DeviceRuntime` serial submission thread per adapter to own the immediate context, fences, texture/readback pools and completion queue. Capture/OCR workers never call the immediate context. On resize, removal or shutdown, stop old-epoch submission, cancel unsubmitted work, drain submitted fences with a bounded timeout, then release resources in ownership order.
- [ ] Keep full frames on the GPU; allow only bounded low-rate preview thumbnails to cross IPC.
- [ ] Define overlay visibility so each overlay appears only when its target is visible under the profile policy; test two concurrent windows, cross-adapter displays, device removed/reset, minimize/restore, borderless mode, foreground switches, resize storms, monitor disconnect, virtual desktops and target close/reopen.
- [ ] Commit with `feat(engine): add multi-target Windows capture`.

## Task 4: Implement normalized geometry and DPI/layout variants

**Files:**
- Create: `src/InfiniTranseon.Engine.Native/src/imaging/coordinate_mapper.cpp`
- Create: `src/InfiniTranseon.Core/Profiles/LayoutVariantSelector.cs`
- Test: `tests/InfiniTranseon.Engine.Tests/coordinate_mapper_tests.cpp`
- Test: `tests/InfiniTranseon.Core.Tests/Profiles/LayoutVariantTests.cs`

**Interfaces:**
- Consumes: `NormalizedRect`, target client size, DPI, rotation, and selected aspect-ratio variant.
- Produces: clamped integer source rectangles and overlay rectangles in physical pixels.

- [ ] Map region coordinates relative to target client content, not desktop coordinates or non-client chrome.
- [ ] Implement explicit layout variants selected by aspect-ratio range and optional resolution hints.
- [ ] Define clamping and minimum-size behavior; surface invalid/shrunken regions instead of silently expanding them.
- [ ] Test Windows scaling 100%–300%, negative virtual-screen coordinates, mixed-DPI monitors, 16:9/16:10/21:9/4:3, and one-pixel rounding boundaries.
- [ ] Commit with `feat(engine): add DPI-safe normalized region mapping`.

## Task 5: Build multi-rate change detection, text detection, and latest-wins scheduling

**Files:**
- Create: `src/InfiniTranseon.Engine.Native/src/imaging/change_detector.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/imaging/text_box_tracker.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/scheduling/region_scheduler.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/scheduling/latest_queue.h`
- Create: `src/InfiniTranseon.Engine.Native/src/scheduling/generation_registry.cpp`
- Test: `tests/InfiniTranseon.Engine.Tests/scheduler_tests.cpp`

**Interfaces:**
- Produces pre-track `DetectionWorkItem{targetInstanceId, captureAreaKey, captureFrameRef, detectionEpoch, profileRevision, deadline}`, bounded `DetectionCandidate{candidateId, bounds, detectionEpoch}`, then post-track `RecognitionWorkItem{sourceGenerationToken, cropLeaseId, frameMetadata, priority, deadline, reason}`. `SourceEventId` is created only after recognized text becomes stable.
- Guarantees: bounded queues, cancellation, P0 priority, and no stale work application.

- [ ] Create a low-resolution global detection plane capped at a 1920-pixel long edge plus change-driven original-resolution tiles/image pyramid for small UI text; keep original-resolution textures for OCR crops and perform change reduction on GPU.
- [ ] Run lightweight change checks on configured regions at their user-selected rates; run unknown-area text detection at a separate default cadence near 1 Hz.
- [ ] Define `AreaMode.UserRegion|FullTarget|RemainingArea`, compute remaining-area masks as target minus user/exclusion regions, and track detected candidates geometrically before recognition. Allocate provisional/stable `TextTrackId` before `RecognitionWorkItem`; create `SourceEventId` only after stable text, with explicit merge/split/disappear/reappear rules. Deduplicate overlap with explicit user regions.
- [ ] Add typewriter stabilization with configurable stable-frame count, minimum delay, maximum wait, and a forced-progress path that remains generation-safe.
- [ ] Reuse a `SourceEventId` only for normalized prefix extension inside a configurable continuation window capped at 5 seconds. Stable replacement, clear/reappear, timeout, semantic reset, merge or split creates a new event; test fixed dialogue boxes that display unrelated lines at the same coordinates.
- [ ] Implement two-dimensional target/priority weighted-deficit scheduling with initial weights 8:4:2:1, EDF within a class, P0 maximum burst 8 and one-level aging after `max(configuredInterval, 500 ms)`. Use separate capacity-1 latest-wins keys for pre-track detection and post-track recognition/stages. Under overload, explicitly degrade/pause/error work rather than silently starving it.
- [ ] Bound spatial association to 9 visited cells, 32 scanned entries per cell and 8 scored candidates per OCR box. Traverse boxes by `(top, left, candidateId)` and break equal scores by `TextTrackId`, giving `O(B + Escan)` with `Escan ≤ 288B`; subdivide an overflowing cell once, then create an uncertain candidate track rather than scanning all T or forcing a wrong match.
- [ ] Test static 4K frames, full-screen motion, typewriter subtitles, rapid menu changes, 50 regions, two targets, cancellation, and generation rollover.
- [ ] Commit with `feat(engine): add multi-rate region scheduler`.

## Task 6: Implement local OCR, preprocessing, and cloud OCR routing

**Files:**
- Create: `src/InfiniTranseon.Engine.Native/src/ocr/ocr_engine.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/ocr/onnx_session_pool.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/ocr/preprocessing_pipeline.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/ocr/readback_ring.cpp`
- Create: `src/InfiniTranseon.Core/Ocr/CloudOcrRouter.cs`
- Create: `src/InfiniTranseon.Core/Ocr/IOcrProvider.cs`
- Test: `tests/InfiniTranseon.Engine.Tests/ocr_pipeline_tests.cpp`
- Test: `tests/InfiniTranseon.Core.Tests/Ocr/CloudOcrRouterTests.cs`

**Interfaces:**
- Local OCR consumes original-resolution crops and produces boxes, lines, confidence, orientation, and model metadata.
- Cloud OCR consumes only an explicitly authorized encoded crop and produces the same normalized `OcrResult` contract.

- [ ] Pin the self-contained ONNX Runtime CPU baseline and supported model/opset matrix in `Directory.Packages.props` and model manifests; record runtime version, model hash, input shape, adapter LUID and execution provider in diagnostics. Do not require a discrete GPU.
- [ ] Implement Windows ML/compatible execution providers behind an explicit adapter only after a benchmark or user choice; promise only tested CPU/DirectML paths by default, and no EP is auto-downloaded.
- [ ] Implement profile-driven grayscale, scaling, contrast, adaptive threshold, color isolation, sharpening, outline suppression, inversion, and alpha cleanup.
- [ ] For CPU OCR, crop and preprocess on GPU, copy only bounded crops into the fixed staging/readback ring, and wait asynchronously for the fence. DeviceRuntime only Map/Unmap and hands a `MappedReadbackLease` to a bounded OCR-prep worker for memcpy/normalize; cap two mapped leases per adapter, 4 MiB copy per dispatch quantum and 20 ms hold time. Ban synchronous full-frame readback and record copy, fence, map and tensorization latency separately.
- [ ] Support fixed-region recognition without neural detection, automatic detector plus recognizer, and user-enabled cloud OCR. CPU ONNX is the common installer/portable baseline; Windows.Media.Ocr is an optional package-identity enhancement only and cannot be required for portable parity.
- [ ] Enforce strict-offline and cloud-crop consent before constructing or calling cloud providers.
- [ ] Derive `OcrExecutionToken{Source, OcrRunId, Attempt, ResultSequence}` for every local/cloud OCR attempt; request, response and IPC carry it, and the generation registry accepts only the current attempt and continuous result sequence.
- [ ] Test tiny text, outlined text, light/dark backgrounds, rotated/vertical metadata, malformed provider responses, model absence, cancellation, and zero-network offline mode.
- [ ] Commit with `feat(ocr): add local and opt-in cloud OCR routing`.

## Task 7: Implement OCR post-processing, line layout, and text stabilization

**Files:**
- Create: `src/InfiniTranseon.Core/Ocr/TextNormalizer.cs`
- Create: `src/InfiniTranseon.Core/Ocr/LineLayoutProcessor.cs`
- Create: `src/InfiniTranseon.Core/Ocr/TextStabilizer.cs`
- Create: `src/InfiniTranseon.Core/Ocr/ConservativeCorrectionService.cs`
- Test: `tests/InfiniTranseon.Core.Tests/Ocr/PostProcessingTests.cs`

**Interfaces:**
- Consumes: OCR boxes/lines plus per-region `LineBreakPolicy`.
- Produces: normalized source text while retaining original text, line geometry, confidence, and speaker metadata.

- [ ] Implement reading order, wrapped-line joining, paragraph grouping, duplicate suppression, punctuation preservation, speaker-name separation, and symbol-noise rejection.
- [ ] Add per-region policies: preserve lines, join paragraph, key/value rows, custom separator, per-box translation, maximum lines, and alignment.
- [ ] Keep exact OCR source for debugging in memory only; logs and ordinary diagnostics receive hashes/lengths, not text.
- [ ] Make correction conservative, glossary-aware, disableable, and forbidden for protected names, short tokens, numbers, and low-confidence fantasy terms.
- [ ] Test `Attack:100 / Defense:100 / Health:200`, CJK punctuation, mixed scripts, repeated UI labels, fantasy names, and typewriter partials.
- [ ] Commit with `feat(ocr): add configurable text post-processing`.

## Task 8: Implement provider adapters, credentials, and declarative REST

**Files:**
- Create: `src/InfiniTranseon.Core/Translation/ITranslationProvider.cs`
- Create: `src/InfiniTranseon.Core/Translation/OnlineProviderService.cs`
- Create: `src/InfiniTranseon.Core/Translation/ProviderRegistry.cs`
- Create: `src/InfiniTranseon.Core/Translation/BuiltIn/`
- Create: `src/InfiniTranseon.Core/Translation/OpenAiCompatibleProvider.cs`
- Create: `src/InfiniTranseon.Core/Translation/Rest/DeclarativeRestProvider.cs`
- Create: `src/InfiniTranseon.Core/Privacy/GenericCredentialStore.cs`
- Create: `src/InfiniTranseon.App/Presentation/Services/BuiltInProviderSpecs.cs`
- Create: `src/InfiniTranseon.Contracts/Translation/declarative-rest.schema.json`
- Test: `tests/InfiniTranseon.Core.Tests/Translation/ProviderContractTests.cs`

**Interfaces:**
- `IAsyncEnumerable<ProviderEvent> StreamAsync(TranslationRequest request, CancellationToken cancellationToken)` runs in a bounded App-owned provider service. `ProviderSnapshot` carries complete cumulative text; `StageExecutionToken.StreamSequence` is the only sequence value, and exactly one `ProviderCompleted`, `ProviderFailed` or `ProviderCancelled` terminal event is allowed. Adapters are DI modules with no WinUI dependency; their serializable request/event DTOs can move behind a worker boundary later.
- `TranslationRequest` includes text, language pair, permitted context, glossary view, execution token, timeout, idempotency key, maximum output characters/tokens, provider billing unit and worst-case cost reservation metadata.

- [x] Keep every advertised built-in provider in one typed `BuiltInProviderSpecs` list that owns its stable ID, display metadata, credential bindings and runtime factory. Runtime registries and settings rows are projections of that list and a parity test rejects registration drift. Built-ins: DeepL, Google Cloud Translation, Azure AI Translator, Baidu Translate and Alibaba Cloud Machine Translation; OpenAI, Anthropic, Gemini, DeepSeek, Qwen/Model Studio and Baidu Qianfan; Azure AI Vision, Google Cloud Vision, Baidu OCR and Tencent Cloud OCR.
- [ ] Audit built-in providers against their golden request/response tests before changing an adapter. Use declarative REST definitions when the protocol fits the schema and hand-written adapters only for SSE, binary OCR, OAuth or HMAC signing. Do not maintain a second embedded provider matrix that can drift from the executable registry.
- [ ] Implement OpenAI-compatible translation/streaming with explicit model, endpoint, timeout, and context controls.
- [ ] Validate adapter-internal `ProviderDeltaSequence` as continuous and terminate an attempt on a raw delta gap. Emit cumulative snapshots whose `StageExecutionToken.StreamSequence` is a monotonic revision that may skip after coalescing; terminal events carry complete final text and the last delta sequence. Reject duplicate terminal events and size/time violations.
- [ ] Implement shared versioned `DeclarativeRestAdapterDefinition` JSON Schema for allowed template variables, credential references, UTF-8/body formats, bounded JSON selectors, SSE framing, status/error mapping, timeout and response limits. Use common golden fixtures in frontend and the Core provider adapters; allow no scripting or executable plugins.
- [ ] Store only opaque credential references in profiles and SQLite; use Win32 Credential Manager generic credentials rather than PasswordVault's small per-app credential limit. Bind every credential to provider ID, allowed scheme/host/port, authentication purpose and proxy policy; origin/auth-template/proxy changes require explicit reconfirmation, and the App-owned provider dispatch boundary validates the binding on every request.
- [ ] Define `CloudOcrCropRequest` and response with target instance, region/track, complete `OcrExecutionToken`, consent-policy revision, encoding, dimensions, byte ceiling and deadline. EngineHost sends only the encoded bounded crop to the App-owned cloud OCR adapter; complete frames remain inside EngineHost and crop bytes are never logged or persisted. Accept only the current run/attempt/sequence.
- [ ] Add per-provider rate limiting, exponential backoff with jitter for retryable errors, one configured retry maximum in latency-sensitive paths, and true cancellation when supported.
- [ ] Isolate HTTP handlers by `(provider, origin, proxy policy)`, disable cookies and automatic redirects by default, and never forward credentials or a request body across origins. Use `ResponseHeadersRead`; enforce header, compressed/decompressed body, JSON depth, SSE event, cumulative character/token, duration and idle-time limits. Test 307/308, compression bombs, slow responses and infinite SSE.
- [ ] Contain every adapter call behind exception mapping, linked cancellation, deadline, request/response byte ceilings and concurrency limits. A fault or hung translation request fails only its channel; a cloud OCR fault affects only its OCR run/region and may fall back to local OCR only when that policy is already configured. Retry only current idempotent generations that still fit request/cost budgets. Test disposal during App shutdown and EngineHost reconnect while cloud OCR requests are in flight.
- [ ] Test secret redaction, HTTP 400/401/403/429/500, malformed JSON, streaming disconnect, proxy behavior, cancellation, and strict offline construction guards.
- [ ] Commit with `feat(translation): add secure provider adapters`.

## Task 9: Implement deterministic multi-channel translation and refinement

**Files:**
- Create: `src/InfiniTranseon.Core/Translation/TranslationOrchestrator.cs`
- Create: `src/InfiniTranseon.Core/Translation/TranslationChannelRunner.cs`
- Create: `src/InfiniTranseon.Core/Translation/ContextBuilder.cs`
- Create: `src/InfiniTranseon.Core/Translation/GlossaryProcessor.cs`
- Test: `tests/InfiniTranseon.Core.Tests/Translation/TranslationOrchestratorTests.cs`

**Interfaces:**
- Consumes: one `TextGeneration` and ordered 1–4 `TranslationChannelDefinition` records.
- Emits: stage-specific `TranslationOutput` events independently for each fixed display slot.

- [ ] Run enabled channels in parallel with independent timeout, cancellation, at most 2 fallbacks, 1 retry per provider attempt and failure state. Enforce per-provider/profile/global semaphores and token buckets. Before dispatch, atomically reserve worst-case cost per provider/billing unit/currency; settle afterward. Unknown pricing is labeled estimate-only and cannot claim strict currency enforcement.
- [ ] Permit NMT or LLM as initial provider; permit at most 2 explicit LLM refinements after either; validate and execute a linear acyclic stage list.
- [ ] Delete any candidate-judge or winner-selection algorithm. A successful channel result belongs to its configured display slot and is not compared automatically.
- [ ] Build context from game name, user description, scene, speaker, glossary, and bounded recent source/translation history according to per-provider permission.
- [ ] Derive one `ChannelExecutionToken` per channel and one `StageExecutionToken` per attempt/stream. Emit initial output as soon as ready; replace only the same immutable slot when the source and channel tokens remain current and the region policy allows live replacement. Preserve stage, attempt, fallback, stream completion, failure, currency and supersession provenance.
- [ ] On translator-group hotkey change, resolve explicit `AllRunning|ForegroundMatched|TargetSet` scope, cancel affected work, create new channel runs, and retranslate currently visible source text without changing foreground focus; if no target matches, report status and never guess.
- [ ] Test two/three simultaneous channels, mixed NMT/LLM, one failure, fallback, late refinement, stale generation, context denial, rate limits, and hotkey switching.
- [ ] Commit with `feat(translation): add deterministic parallel translation channels`.

## Task 10: Add translation memory, cautious similarity, glossary, and corrections

**Files:**
- Create: `src/InfiniTranseon.Core/Translation/TranslationMemory.cs`
- Create: `src/InfiniTranseon.Core/Translation/TranslationCacheKey.cs`
- Create: `src/InfiniTranseon.Core/Translation/CorrectionStore.cs`
- Create: `src/InfiniTranseon.Core/Storage/SqliteDatabase.cs`
- Test: `tests/InfiniTranseon.Core.Tests/Translation/TranslationMemoryTests.cs`

**Interfaces:**
- Produces exact and optional similarity hits with provenance; corrections have explicit profile/region/language/glossary scopes.
- Cache keys include provider, model, language pair, normalized source, style/prompt version, glossary version, profile policy, and context digest when context affects output.

- [ ] Always provide a bounded memory LRU. Add persistent SQLite translation memory only as a separate per-profile opt-in from history; when both are disabled, write no source text, translated text, reversible digest, embedding or vector to SQLite.
- [ ] Use exact matching by default; make fuzzy matching opt-in, forbidden for short strings/IDs/numeric rows, and require normalized similarity plus language-aware safeguards.
- [ ] Add glossary placeholder protection for providers without native glossary support and direct prompt instructions for LLMs.
- [ ] Store corrections separately from provider cache, with explicit scope, author timestamp, undo, and precedence rules.
- [ ] Test provider/model/prompt/glossary/context invalidation, Unicode normalization, collisions, fuzzy false positives, database corruption, size eviction, concurrent readers, history-off/cache-off zero text persistence and every consent combination.
- [ ] Commit with `feat(translation): add safe translation memory`.

## Task 11: Render native click-through overlays with stable multi-result slots

**Files:**
- Create: `src/InfiniTranseon.Engine.Native/src/overlay/overlay_window.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/overlay/overlay_renderer.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/overlay/text_layout.cpp`
- Create: `src/InfiniTranseon.Engine.Native/src/overlay/background_treatment.cpp`
- Test: `tests/InfiniTranseon.Engine.Tests/overlay_layout_tests.cpp`
- Test: `tests/InfiniTranseon.IntegrationTests/Overlay/OverlayBehaviorTests.cs`

**Interfaces:**
- Consumes: a per-target complete immutable `OverlayDesiredState{RuntimeEpoch, TargetInstanceId, OverlayRevision, regions, orderedSlots}` snapshot. EngineHost may latest-win intermediate revisions and acknowledges the last applied revision; no required delta update crosses IPC.
- Guarantees: non-activating, click-through, topmost, taskbar/Alt+Tab-hidden, capture-excluded, DPI-aware overlays.

- [ ] Create per-target overlay ownership and a foreground/cloaked/minimized/virtual-desktop/z-order visibility state machine; overlay creation/update never activates or resizes the target game window.
- [ ] Run HWND/message loop, DirectComposition visual tree, D2D contexts and `Commit` on the dedicated overlay thread. DeviceRuntime owns D3D immediate submission and fences; define the bounded handoff and shutdown order between them.
- [ ] Require and verify capture exclusion before rendering. If `WDA_EXCLUDEFROMCAPTURE` fails, stop the affected overlay and expose an explicit compatibility error rather than risking a feedback loop.
- [ ] Implement full replacement, blur/translucent, offset, floating/fixed panel, opaque color, temporal background cache, automatic contrast, and no-cover modes. Passive floating-panel hover uses low-rate global cursor-position hit testing and never removes `WS_EX_TRANSPARENT`.
- [ ] Reserve no more than 4 fixed multi-translator slots before requests complete; render waiting/streaming/success/fallback/timeout/failure/cancelled states independently without reflow and preserve configured labels/order/colors.
- [ ] Implement text fitting, wrapping, max lines, font reduction floor, outline, alignment, padding, overflow indicator, and fixed-panel fallback; never require scrolling in-game.
- [ ] Make refinement replace only its immutable channel slot after validating the complete source → channel → stage token hierarchy and use minimum dwell/crossfade rules that honor reduced motion and avoid flicker.
- [ ] Test focus preservation, capture exclusion, 4K, 300% DPI, mixed monitors, transparent backgrounds, long CJK text, 4 slots, target resize, and rapid generations.
- [ ] Commit with `feat(overlay): add stable native translation overlays`.

## Task 12: Implement performance governor and explicit degradation lifecycle

**Files:**
- Create: `src/InfiniTranseon.Core/Scheduling/PerformanceGovernor.cs`
- Create: `src/InfiniTranseon.Core/Scheduling/DegradationPolicy.cs`
- Create: `src/InfiniTranseon.Engine.Native/src/diagnostics/performance_sampler.cpp`
- Test: `tests/InfiniTranseon.Core.Tests/Scheduling/PerformanceGovernorTests.cs`

**Interfaces:**
- Consumes: CPU, RAM, GPU time when available, OCR/translation latency, queue replacement, WGC frame-arrival rate, and region priorities. Frame-arrival rate is a capture-pressure signal and must never be labeled as the game's actual FPS.
- Emits: `DegradationStarted`, `DegradationChanged`, `DegradationRecovered` with cause, before/after values, impact, recovery condition and per-region policy revision/EngineHost acknowledgement.

- [ ] Define Eco, Balanced, Performance, and Custom presets as initial values, never as hidden immutable modes.
- [ ] Apply only documented actions: lengthen low-priority intervals, reduce unknown-area detector cadence, pause optional remaining-area scan, choose a configured smaller OCR model, or pause optional refinement.
- [ ] Never auto-change user-locked region settings and never silently switch cloud/local providers or network privacy modes. If hard capacity, OOM or device loss makes a lock impossible, discard old work and enter an explicit paused/error state.
- [ ] Add hysteresis and minimum dwell to prevent oscillation; restore settings in reverse order when recovery conditions persist.
- [ ] Apply policy revisions atomically across IPC with acknowledgement, rejection reason and reconnect snapshot; test lock/unlock races, resource spikes, sustained overload, all regions locked, two targets, unavailable GPU metrics, refinement pause/resume and restart during degradation.
- [ ] Commit with `feat(core): add visible performance governance`.

## Task 13: Implement history, state logs, retention, and local crash reports

**Files:**
- Create: `src/InfiniTranseon.Core/Storage/HistoryRepository.cs`
- Create: `src/InfiniTranseon.Core/Storage/RecentTranslationBuffer.cs`
- Create: `src/InfiniTranseon.Core/Diagnostics/StatusEventLog.cs`
- Create: `src/InfiniTranseon.Core/Diagnostics/LogRedactor.cs`
- Create: `src/InfiniTranseon.Core/Diagnostics/CrashReportBuilder.cs`
- Test: `tests/InfiniTranseon.Core.Tests/Diagnostics/DiagnosticsStorageTests.cs`

**Interfaces:**
- History stores grouped text generations only when enabled for that profile.
- `RecentTranslationBuffer` always provides a profile-scoped in-memory read-only snapshot for the current session, capped at 200 source events or 5 MiB and cleared on profile stop, explicit clear or App exit.
- Status logs always store structured states without OCR/translation text; default crash packages contain local structured metadata, stack addresses and module/version lists but no process-memory dump.

- [ ] Implement profile-scoped history with time and byte limits, text-only default, channel/refinement provenance, latency, cost estimate, cache state, and failure metadata.
- [ ] Implement history pagination with `(ProfileId, CapturedAtUtc DESC, SourceEventId DESC)` index and matching keyset cursor; prohibit deep-page `OFFSET` and test stable pagination over one million synthetic rows.
- [ ] Feed the Recent Translations panel from `RecentTranslationBuffer` even when persistent history is disabled. Prove that this path performs no SQLite text write and releases all entries on profile stop.
- [ ] Default history to disabled; when the user enables it, apply 30 days and 500 MB until changed.
- [ ] Implement structured rotating state logs for startup, target lifecycle, provider status, performance, degradation, update, and error codes without source/translated text. Runtime events expose stable `errorCode + messageKey + typed arguments`; they never send English display sentences to the UI.
- [ ] Create local structured crash metadata with redaction preview and no upload endpoint. Memory-bearing dumps are a separate diagnostic-mode action with explicit consent, private ACL, a warning that text/images/keys may be present, and a short retention limit; never claim such dumps are fully redacted.
- [ ] Test history disabled, cleanup boundaries, crash during write, redaction of API keys/headers/paths/text, database corruption, and export cancellation.
- [ ] Commit with `feat(core): add private history and diagnostics storage`.

## Task 14: Add optional local translation model worker

**Files:**
- Create: `src/InfiniTranseon.ModelWorker/InfiniTranseon.ModelWorker.csproj`
- Create: `src/InfiniTranseon.ModelWorker/Program.cs`
- Create: `src/InfiniTranseon.Core/Translation/Local/LocalWorkerClient.cs`
- Create: `src/InfiniTranseon.Core/Translation/Local/ModelCatalogService.cs`
- Create: `src/InfiniTranseon.Core/Translation/Local/ModelDownloadService.cs`
- Test: `tests/InfiniTranseon.IntegrationTests/LocalModels/LocalWorkerTests.cs`

**Interfaces:**
- Uses a dedicated versioned named pipe with package/current-user SID ACL, expected PID, one-time bootstrap secret delivered through the sole `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` inherited anonymous-pipe read handle, `WorkerSessionEpoch`, length-prefixed messages and bounded text payloads; no image or arbitrary file-path command crosses the pipe.
- Produces local provider capabilities and translation outputs identical to online providers.

- [ ] Start the worker only when a configured installed local model is needed; stop it after an idle timeout unless a profile pins it warm.
- [ ] Download models only after explicit user action; show size/license, verify HTTPS and checksum/signature, write atomically, and clean partial downloads.
- [ ] Constrain model paths to the managed model directory and reject traversal, symlinks/reparse points outside it, and manifests not signed by the model-catalog trust root. Audit every model license separately from Apache-2.0 application code.
- [ ] Launch ModelWorker in an AppContainer/LPAC-equivalent sandbox with no network capability, minimal directory ACL, a separate Job Object and process/memory limits. Inherit only the bootstrap read handle through an explicit handle list and close it immediately after handshake; inherit no other handles. The secret never enters command line, environment, logs or files. A restricted token alone is insufficient. Worker restart changes `WorkerSessionEpoch` and invalidates every old request/result.
- [ ] Implement CPU-first INT8 translation with user-selectable compatible acceleration and strict memory/concurrency caps.
- [ ] Test missing model, cancel, disk full, checksum failure, forged/unknown-key catalog signature, tampered entry, catalog rollback, dual-key rotation, worker crash/restart, stale epoch, rogue same-user pipe client, oversized messages, path traversal, offline installed-model use, no automatic download, and actual DNS/TCP denial from inside the sandbox.
- [ ] Commit with `feat(local): add opt-in local translation worker`.

## Task 15: Implement update checks, packaging, signatures, and profile-safe portability

**Files:**
- Create: `src/InfiniTranseon.Core/Updates/GitHubReleaseUpdateService.cs`
- Create: `src/InfiniTranseon.Core/Updates/SignatureVerifier.cs`
- Create: `packaging/installer.wxs`
- Create: `packaging/portable-manifest.json`
- Create: `.github/workflows/build-release.yml`
- Test: `tests/InfiniTranseon.Core.Tests/Updates/UpdateTests.cs`

**Interfaces:**
- Update service returns metadata only until the user approves download; installer launch occurs only after cryptographic verification.
- Produces an unsigned installer and portable ZIP with identical base capabilities, with an Ed25519-signed release manifest and explicit user-facing publisher warnings.

- [ ] Check GitHub Releases only outside strict offline mode, only when no capture target is active and the main UI is visible, or after explicit user action; never auto-download or auto-install.
- [ ] Treat GitHub metadata as untrusted. Verify the canonical manifest against the embedded/rotated Ed25519 trust root, then verify bound version, channel, architecture, artifact size, SHA-256, explicit code-signing policy and persisted anti-downgrade state. Verify the exact publisher when the policy is `authenticode`; show an unavoidable warning when it is `unsigned`.
- [ ] Package CPU OCR baseline and required runtimes; exclude optional acceleration packs and local translation models. Generate LICENSE, NOTICE, SBOM, source archive and third-party/model/font/dictionary license reports, failing CI on incompatible or unknown licenses.
- [ ] Preserve user data and credentials across installer and portable updates; never write secrets into the portable directory.
- [ ] Test tampered metadata/artifact, revoked/unknown signer, interrupted download, downgrade, offline mode, portable update, and installer refusal on unsupported Windows versions.
- [ ] Commit with `build: add signed GitHub release pipeline`.

## Task 16: Build benchmarks, privacy tests, and end-to-end release gates

**Files:**
- Create: `benchmarks/GameOcrBench/README.md`
- Create: `benchmarks/GameOcrBench/BenchmarkRunner.cs`
- Create: `tests/InfiniTranseon.IntegrationTests/EndToEnd/MultiTargetPipelineTests.cs`
- Create: `tests/InfiniTranseon.IntegrationTests/Privacy/StrictOfflineTests.cs`
- Create: `tests/InfiniTranseon.IntegrationTests/Immersion/ForegroundWindowTests.cs`
- Create: `docs/testing/backend-release-checklist.md`

**Interfaces:**
- Uses licensed/synthetic fixtures for clean, outlined, shadowed, small, moving, CJK, Latin, 720p, 1080p, 1440p, and 4K text.
- Produces machine-readable median/p95 latency, CER, line accuracy, missed/false detections, CPU/GPU/RAM/VRAM, cache, provider error, and cost reports.

- [ ] Add deterministic fake providers, capture test windows, controllable typewriter animation, failure injection, and virtual clock support.
- [ ] Verify full pipeline capture → detection → tracked recognition → up to 4 parallel translations → up to 2 refinements → fixed overlay slots with `SourceGenerationToken`, `OcrExecutionToken`, `ChannelExecutionToken`, `StageExecutionToken` and `OverlayRevision` stale-result rejection, including attempt delay, stream reordering, EngineHost restart, target recreation and profile revision.
- [ ] Verify strict offline mode with a network-deny harness and assert zero DNS/socket attempts.
- [ ] Run 30-minute two-target 4K soak tests and assert bounded queues, fair scheduling, stable memory, no old overlay generation, and no focus theft.
- [ ] Measure actual committed CPU/GPU bytes from WGC pools, owned textures, detection pyramids, overlay surfaces, readback rings, OCR sessions/tensors, IPC and caches. Assert every pool against `RuntimeCapabilities` and the current `RuntimeBudgetSnapshot`, including old/new device-epoch resources coexisting during reset; admission must reject or explicitly degrade before peak bytes exceed budget.
- [ ] Inject sustained P0 work while P1–P3 remain eligible and verify weighted deficit, maximum burst and aging provide bounded service; under true overload verify lower work is explicitly degraded/paused instead of silently starved.
- [ ] Verify provider cumulative snapshots, sequence gaps, duplicate terminal events and target-level full overlay desired-state snapshots with acknowledgement; dropping intermediate updates must still converge to the final state.
- [ ] Validate candidate controllable goals: idle CPU <1%, unchanged-screen CPU <2%, overlay render P95 <2 ms, cached translation P95 <10 ms and base RAM <400 MB. Record CPU/GPU/RAM, target count/resolution, sample window and P50/P95/P99; report provider network latency separately and fail only after release-matrix calibration.
- [ ] Measure the uncached local pipeline latency for P0 regions from change detection through OCR to provider dispatch (excluding provider network time) and validate the candidate P95 <300 ms gate under the same recording rules as the other candidate goals.
- [ ] Add OCR quality gates: measure character error rate, line accuracy and missed/false detection on the CJK/Latin game-font fixture set (clean, outlined, shadowed, small, moving) for every model in the frozen model matrix; block release below the thresholds calibrated in `docs/superpowers/specs/2026-07-20-model-selection-review.md`.
- [ ] Split deterministic unit tests, virtual-frame-source integration, and interactive self-hosted Windows 11 GPU/DWM labs. Run `dotnet test`, `ctest`, benchmark smoke, multi-DPI/multi-monitor/HDR/device-removal/capture-exclusion/focus labs, plus manual release gates; strict-offline network denial applies to the complete process tree.
- [ ] Commit with `test: add end-to-end performance privacy release gates`.

## Backend definition of done

- Two or more capture targets run concurrently with independent profiles, regions, priorities, OCR, translators, overlay styles, and histories.
- A 4K target uses downscaled global detection plus original-resolution OCR crops and never runs periodic full-frame OCR.
- Each source generation can drive up to 4 independent translation channels, each with bounded fallback/retry/refinement, and fixed overlay slots without user selection or LLM judging.
- Every late result is rejected; queues remain bounded under overload.
- Target moves, resize, DPI changes, monitor changes, minimize/restore, and closure preserve or explicitly suspend correct region mapping.
- Strict offline mode produces zero network traffic, and cloud OCR never receives pixels without explicit profile consent.
- Logs contain no OCR/translation text or secrets; history obeys per-profile consent and retention.
- Locked regions are never automatically reconfigured; impossible capacity produces explicit pause/error while all other degradation is visible, reversible and logged.
- Installer and portable GitHub artifacts verify signatures and never bundle optional local translation models.
- OCR quality (CER/line accuracy on the game-font fixture set) and uncached local pipeline latency meet the thresholds calibrated in the model-selection review and release matrix; provider network latency is always reported separately.
