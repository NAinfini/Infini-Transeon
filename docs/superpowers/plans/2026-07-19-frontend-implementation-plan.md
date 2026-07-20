# Infini-Transeon Frontend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an accessible Windows 11 control application that lets users configure powerful OCR/translation profiles once, then play without focus changes, modal decisions, or per-line interaction.

**Architecture:** A .NET 10 WinUI 3 application uses feature-scoped MVVM, shared immutable contracts, and a single runtime state store. The app has three deliberate layers of complexity: Profile Center, four-step Setup Wizard, and Optical Workbench; the runtime UI collapses to tray controls, hotkeys, non-activating status feedback, history, and diagnostics.

**Tech Stack:** C# 14, .NET 10 LTS, Windows App SDK 2.2 stable, WinUI 3, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, SQLite-backed services supplied by the backend plan, xUnit, and Windows UI Automation/FlaUI smoke tests.

**Runtime Contract:** Process ownership, immutable snapshots, execution tokens, bounded queues, data structures, Big-O and performance governance follow `docs/superpowers/specs/2026-07-19-runtime-architecture-design.md`; frontend state must not invent a conflicting lifecycle.

## Global Constraints

- Target Windows 11 x64 only, API baseline build 22621 (22H2); Windows 10 is not supported (see `docs/adr/2026-07-20-adr-002-windows-11-minimum.md`). Use solid backgrounds where composition materials are unavailable (remote sessions, transparency disabled by user or policy).
- Use System/Light/Dark themes, Windows high contrast, reduced motion, and 100%–300% DPI.
- Use Segoe UI Variable, a 14 epx normal body minimum, a 12 epx auxiliary-text minimum, and a 32×32 epx minimum compact-mode pointer target.
- All application-authored user-visible strings live in `.resw` resources; ship `en-US` and `zh-CN` application UI first, with fallback to `en-US`. Redacted external-provider messages may appear only in an explicitly unlocalized technical-details section.
- Never show a modal, candidate-selection prompt, update prompt, credential prompt, or download prompt while a capture target is running in the foreground.
- A region supports 1–4 translation channels and at most 2 refinement steps per channel; results display automatically in fixed, preconfigured slots without requiring user selection.
- No code plugins, community profile browser, automatic model downloads, automatic update installation, or remote crash-log submission.
- Preserve every accepted feature in the first public release; task order is implementation sequencing, not a reduced public MVP.
- Use semantic WinUI controls, visible keyboard focus, `AutomationProperties.Name`, text-plus-icon statuses, undo for reversible destructive actions, and confirmation for irreversible actions.
- Any UI action that changes a running profile must be explicit and persisted atomically. The UI must show `Applying` within 250 ms, then change to `Applied` only after EngineHost acknowledgement; rejection restores the persisted value and exposes retry details.

---

## Planned file structure

```text
InfiniTranseon.sln
Directory.Build.props
Directory.Packages.props
src/InfiniTranseon.App/
  App.xaml
  App.xaml.cs
  Program.cs
  Shell/AppShell.xaml
  Shell/AppShellViewModel.cs
  Navigation/NavigationService.cs
  State/RuntimeStateStore.cs
  Theme/ThemeService.cs
  Localization/LocalizationService.cs
  Resources/en-US/Resources.resw
  Resources/zh-CN/Resources.resw
  Features/ProfileCenter/
  Features/SetupWizard/
  Features/Workbench/
  Features/TranslationChannels/
  Features/Glossary/
  Features/OverlayStyles/
  Features/RuntimeControls/
  Features/History/
  Features/Settings/
  Features/Diagnostics/
  Controls/
  Converters/
src/InfiniTranseon.Contracts/
  Profiles/ProfileContracts.cs
  Runtime/RuntimeContracts.cs
  Translation/TranslationContracts.cs
  Diagnostics/DiagnosticContracts.cs
tests/InfiniTranseon.App.Tests/
tests/InfiniTranseon.App.UiTests/
```

`InfiniTranseon.App` owns presentation, user intent and bounded online-provider adapters. It never captures or retains complete frames, performs local OCR, renders the game overlay, or stores secrets directly; it may forward only an explicitly authorized bounded crop to cloud OCR. `InfiniTranseon.Contracts` is presentation-neutral and may not reference WinUI.

## Cross-plan dependencies

| Frontend capability | May develop against | Real integration gate |
|---|---|---|
| Capture target probe | Task 1 `FakeCaptureProbe` | Backend Task 3 |
| OCR test | Task 1 `FakeOcrProbe` | Backend Task 6 |
| Provider test | Task 1 `FakeTranslationProbe` | Backend Task 8 |
| Overlay preview | Task 1 `FakeOverlayPreviewRenderer` | Backend Task 11 |

Frontend tasks may complete view/state behavior against contract fakes, but cannot mark real end-to-end integration complete before the corresponding backend gate passes.

## Task 0: Prove tray, focus preservation, overlay messaging, and EngineHost IPC

**Files:**
- Create: `spikes/InfiniTranseon.PlatformSpike/`
- Create: `docs/architecture/platform-spike-results.md`
- Test: `tests/InfiniTranseon.App.UiTests/Spikes/PlatformBoundaryTests.cs`

**Interfaces:**
- Proves: C# Win32 `Shell_NotifyIcon` plus hidden message HWND, versioned named-pipe handshake to EngineHost, native overlay-owned game notifications, and foreground/focus/mouse-capture preservation.

- [ ] Build a disposable spike that opens a WinUI settings window, closes it to a Win32 tray icon, controls a mock EngineHost over a secured named pipe, and renders a native non-activating click-through overlay.
- [ ] Verify tray right-click behavior using both XAML and native menus; select the implementation that does not activate or capture input from the foreground game.
- [ ] Verify overlay status messages are rendered by EngineHost, auto-dismiss, contain no clickable controls, and can be hidden with a hotkey.
- [ ] Record foreground HWND, keyboard focus HWND, mouse capture HWND, latency, behavior across supported Windows 11 releases, DPI behavior, and recovery after Explorer or EngineHost restart.
- [ ] Block all later UI tasks until the spike proves the boundary or the architecture document is revised.
- [ ] Commit with `spike(ui): validate tray overlay and EngineHost boundary`.

## Task 1: Bootstrap the WinUI application and presentation contracts

**Files:**
- Create: `InfiniTranseon.sln`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `src/InfiniTranseon.App/InfiniTranseon.App.csproj`
- Create: `src/InfiniTranseon.Contracts/InfiniTranseon.Contracts.csproj`
- Create: `src/InfiniTranseon.App/Program.cs`
- Create: `src/InfiniTranseon.App/App.xaml`
- Create: `src/InfiniTranseon.App/App.xaml.cs`
- Create: `src/InfiniTranseon.App/State/RuntimeCapabilitiesService.cs`
- Test: `tests/InfiniTranseon.App.Tests/BootstrapTests.cs`

**Interfaces:**
- Produces: `IProfileService`, `IRuntimeControlService`, `IHistoryService`, `ISettingsService`, `IDiagnosticsService`, `ISecretReferenceService`, and read-only `IRuntimeCapabilitiesService` abstractions plus composite runtime-generation, `RuntimeCapabilities`, `RuntimeBudgetSnapshot` and acknowledgement contracts for ViewModels.
- Constraint: UI contracts use IDs, metadata, and thumbnail handles; they never carry full capture frames or secret values.

- [ ] Add the solution, projects, centralized package versions, nullable reference types, warnings-as-errors, x64 configuration, and Windows 11 (build 22621) minimum target.
- [ ] Write bootstrap tests that resolve every presentation service from dependency injection and assert no ViewModel depends on a native engine type.
- [ ] Make `IRuntimeCapabilitiesService` expose the protocol safety ceilings and latest dynamic budget snapshot, including reconnect revision. ViewModels display localized disabled reasons and never derive their own limits.
- [ ] Implement `Program.cs` composition so service construction failures open a local recovery window instead of reporting fake success.
- [ ] Run `dotnet test tests/InfiniTranseon.App.Tests/InfiniTranseon.App.Tests.csproj -c Debug`; expect all bootstrap tests to pass.
- [ ] Commit with `feat(ui): bootstrap WinUI application shell`.

## Task 2: Create the Fluent design system, theme service, and accessible primitives

**Files:**
- Create: `src/InfiniTranseon.App/Theme/ThemeService.cs`
- Create: `src/InfiniTranseon.App/Theme/DesignTokens.xaml`
- Create: `src/InfiniTranseon.App/Theme/ControlStyles.xaml`
- Create: `src/InfiniTranseon.App/Controls/StatusBadge.xaml`
- Create: `src/InfiniTranseon.App/Controls/AccessibleIconButton.xaml`
- Create: `src/InfiniTranseon.App/Controls/InlineInfoBar.xaml`
- Test: `tests/InfiniTranseon.App.Tests/Theme/ThemeServiceTests.cs`
- Test: `tests/InfiniTranseon.App.UiTests/Accessibility/PrimitiveAccessibilityTests.cs`

**Interfaces:**
- Produces: `ThemeMode.System|Light|Dark`, `DensityMode.Comfortable|Compact`, minimum hit-target metrics, and reusable status components.
- Emits: `ThemeChanged` without reconstructing the navigation frame.

- [ ] Define spacing, corner, typography, focus, semantic color, overlay preview, and density tokens; do not copy 8–10 px prototype text.
- [ ] Add System/Light/Dark behavior, Windows high-contrast binding, Mica capability checks with a solid fallback where composition is unavailable, and reduced-motion resources.
- [ ] Implement focus-visible styles, correct Tab order, Label/LabelledBy, HelpText, Value, Selection, ExpandCollapse, RangeValue and Toggle patterns as appropriate; do not mechanically assign duplicate accessible names.
- [ ] Add UI automation tests for keyboard focus, 200% text scaling, high contrast, and non-color status names.
- [ ] Run unit and UI smoke tests; expect no clipped labels at 1280×720 and no unnamed interactive controls.
- [ ] Commit with `feat(ui): add Fluent accessible design system`.

## Task 3: Implement localization and culture-aware formatting

**Files:**
- Create: `src/InfiniTranseon.App/Localization/LocalizationService.cs`
- Create: `src/InfiniTranseon.App/Localization/DisplayFormatter.cs`
- Create: `src/InfiniTranseon.App/Resources/en-US/Resources.resw`
- Create: `src/InfiniTranseon.App/Resources/zh-CN/Resources.resw`
- Test: `tests/InfiniTranseon.App.Tests/Localization/LocalizationTests.cs`

**Interfaces:**
- Produces: `FormatLatency`, `FormatBytes`, `FormatCurrencyEstimate`, `FormatTimestamp`, localized language names, untranslated provider/brand identifiers, and edits to global versioned `ApplicationSettings{UiLanguage, FormattingRegionMode, FormattingRegion}` rather than profile fields.
- Consumes: typed resource keys only; no ViewModel contains literal user-visible copy.

- [ ] Add English and Simplified Chinese resource catalogs for shell, setup, workbench, overlay, history, settings, errors, privacy, and updater copy.
- [ ] Implement runtime UI-language selection plus a separate “number/date format” option that follows the system by default; show restart-required only when WinUI cannot refresh a resource safely.
- [ ] Format dates, numbers, currencies, storage sizes, and plural counts with the selected formatting region; never translate DeepL, OpenAI, protocol names, model IDs or API identifiers.
- [ ] Test missing-resource fallback, long German-like pseudo-localized strings, CJK line breaking, and right-to-left layout readiness.
- [ ] Commit with `feat(ui): add localized resource and formatting system`.

## Task 4: Build the application shell and Profile Center

**Files:**
- Create: `src/InfiniTranseon.App/Shell/AppShell.xaml`
- Create: `src/InfiniTranseon.App/Shell/AppShellViewModel.cs`
- Create: `src/InfiniTranseon.App/Navigation/NavigationService.cs`
- Create: `src/InfiniTranseon.App/Features/ProfileCenter/ProfileCenterPage.xaml`
- Create: `src/InfiniTranseon.App/Features/ProfileCenter/ProfileCenterViewModel.cs`
- Create: `src/InfiniTranseon.App/Features/ProfileCenter/ProfileCard.xaml`
- Test: `tests/InfiniTranseon.App.Tests/ProfileCenter/ProfileCenterViewModelTests.cs`

**Interfaces:**
- Consumes: `ProfileSummary`, `TargetMatchState`, `RuntimeHealthSummary`.
- Produces intents: `StartProfile(profileId)`, `PauseProfile(profileId)`, `EditProfile(profileId)`, `DuplicateProfile(profileId)`, `ExportProfile(profileId)`.

- [ ] Implement navigation for Profiles, Running Targets, History, Services & Models, Settings, and Diagnostics while keeping Profiles as the home page.
- [ ] Build responsive profile cards with stable outer dimensions while preserving each source aspect ratio by letterboxing; show actual resolution/scaling, clear match state, language direction, area count, and one primary action.
- [ ] Add empty, loading, target-missing, service-unhealthy, active, and multi-target states without blocking unrelated profiles.
- [ ] Add undo for profile deletion and explicit sanitization summary before export.
- [ ] Test keyboard navigation, long profile names, 100 profiles using virtualization, and profile start failure recovery.
- [ ] Commit with `feat(ui): add profile-centered application shell`.

## Task 5: Implement the four-step Setup Wizard

**Files:**
- Create: `src/InfiniTranseon.App/Features/SetupWizard/SetupWizardPage.xaml`
- Create: `src/InfiniTranseon.App/Features/SetupWizard/SetupWizardViewModel.cs`
- Create: `src/InfiniTranseon.App/Features/SetupWizard/Steps/CaptureTargetsStep.xaml`
- Create: `src/InfiniTranseon.App/Features/SetupWizard/Steps/LanguagesAndServicesStep.xaml`
- Create: `src/InfiniTranseon.App/Features/SetupWizard/Steps/RegionsStep.xaml`
- Create: `src/InfiniTranseon.App/Features/SetupWizard/Steps/TestAndSaveStep.xaml`
- Create: `src/InfiniTranseon.App/Features/Services/ProviderSetupControl.xaml`
- Create: `src/InfiniTranseon.App/Features/Services/CredentialEditor.xaml`
- Create: `src/InfiniTranseon.App/Features/Services/ProviderProbeViewModel.cs`
- Test: `tests/InfiniTranseon.App.Tests/SetupWizard/SetupWizardTests.cs`

**Interfaces:**
- Consumes: `CapturableTargetSummary`, `RuntimeCapabilities`, `RuntimeBudgetSnapshot`, `OcrProbeResult`, `TranslationProbeResult`, `OverlayProbeResult`.
- Produces: validated `ProfileDraft` and atomic `SaveProfileDraft` intent.

- [ ] Implement draft persistence, Back/Next navigation, direct step navigation only after prerequisites pass, unsaved-change protection, and a distinction between saveable drafts and runnable profiles with explicit remaining blockers.
- [ ] Show capturable windows, displays, and desktop regions in stable large card frames while preserving each source aspect ratio with letterboxing and displaying actual resolution/scaling; support multiple targets and avoid auto-binding a closed window to a similar title.
- [ ] Show hard and currently available target capacity before selection. A ninth target under v1 remains in the draft as disabled with a localized reason; reconnect budget changes revalidate without deleting the user's choice.
- [ ] Build reusable provider, credential and probe controls first, then let users select language, local/cloud OCR, one initial translation channel, optional persistent translation memory, and optional history retention without exposing advanced thresholds.
- [ ] Provide region drawing, full-target mode, mixed user regions plus remaining-area scan, and a clear route to the advanced workbench.
- [ ] Implement capture/OCR/translation/overlay probe UI against Task 1 contract fakes, then complete real integrations only after backend Tasks 3/6/8/11. Errors remain inline with direct actions such as “Edit API key,” “Use borderless window mode,” or “Choose another window.” Detect exclusive fullscreen, protected content, elevated-process mismatch, capture exclusion failure and unsupported overlay/capture paths.
- [ ] On the final step, explain close-to-tray behavior, show actual registered hotkey status, let the user choose close-button behavior, and show how to reopen or fully exit the app.
- [ ] Test draft resume, offline mode, no-provider state, DPI changes during setup, and save rollback after persistence failure.
- [ ] Commit with `feat(ui): add guided profile setup`.

## Task 6: Build the Optical Workbench and region editor

**Files:**
- Create: `src/InfiniTranseon.App/Features/Workbench/OpticalWorkbenchPage.xaml`
- Create: `src/InfiniTranseon.App/Features/Workbench/OpticalWorkbenchViewModel.cs`
- Create: `src/InfiniTranseon.App/Features/Workbench/RegionTree.xaml`
- Create: `src/InfiniTranseon.App/Features/Workbench/CaptureCanvas.xaml`
- Create: `src/InfiniTranseon.App/Features/Workbench/RegionInspector.xaml`
- Create: `src/InfiniTranseon.App/Features/Workbench/PerformanceStatusStrip.xaml`
- Test: `tests/InfiniTranseon.App.Tests/Workbench/WorkbenchTests.cs`

**Interfaces:**
- Consumes: normalized region geometry, target thumbnail stream capped at 10 FPS, region runtime state, `RuntimeCapabilities`, `RuntimeBudgetSnapshot`, and performance snapshots.
- Produces: add/move/resize/order/rename/duplicate/delete region commands and batched profile changes.

- [ ] Implement target/region tree with multi-selection, drag ordering, keyboard reordering, arbitrary user names, priorities P0–P3, scan interval, and lock-do-not-auto-change state; explain that impossible capacity causes explicit pause/error rather than unbounded work.
- [ ] Preserve over-limit regions as disabled rows with the exact localized admission reason and required/available capacity. Test 257 regions, a lower reconnect budget, re-enable after capacity recovery, and verify no silent truncation.
- [ ] Implement region drawing with snapping, exclusion zones, normalized coordinates, zoom/pan, aspect-ratio variants, and numeric keyboard editing. Make the region tree a complete nonvisual alternative exposing target, coordinates, size, order, enabled state and relationships to assistive technology.
- [ ] Group inspector settings into Basic, OCR, Translation Channels, Overlay, Layout & Line Breaks, and Performance & Degradation.
- [ ] Add per-region line policies: preserve OCR lines, join paragraph, key/value rows, custom separator, maximum lines, alignment, and overflow behavior.
- [ ] Replace the oversized performance bars with a single health strip and expandable request trace; preserve all values as tabular numbers.
- [ ] Add undo/redo for every canvas mutation and warn before leaving with unsaved changes.
- [ ] Test 4K thumbnails, 300% DPI, target resize, two windows, 50 regions, keyboard-only region editing, and profile save conflict handling.
- [ ] Commit with `feat(ui): add optical workbench and region editor`.

## Task 7: Replace the old candidate selector with Translation Channels

**Files:**
- Create: `src/InfiniTranseon.App/Features/TranslationChannels/TranslationChannelsEditor.xaml`
- Create: `src/InfiniTranseon.App/Features/TranslationChannels/TranslationChannelsViewModel.cs`
- Create: `src/InfiniTranseon.App/Features/TranslationChannels/TranslationChannelCard.xaml`
- Create: `src/InfiniTranseon.App/Features/TranslationChannels/ChannelTestResults.xaml`
- Test: `tests/InfiniTranseon.App.Tests/TranslationChannels/TranslationChannelsTests.cs`

**Interfaces:**
- Consumes: `TranslationChannelDefinition`, `ProviderCapability`, `ChannelProbeResult`.
- Produces: ordered 1–4 channels, each with initial provider, at most 2 fallbacks, at most 2 refinement steps, context permissions, cache policy, immutable display slot, concurrency policy and cost budget.

- [ ] Remove “LLM judge,” “best candidate,” and “select result for overlay” concepts from all production copy and state models.
- [ ] Allow NMT or LLM as the initial provider; allow at most 2 explicit LLM refinement steps after either; reject dependency cycles, over-limit pipelines and empty enabled channels.
- [ ] Allow users to enable up to 4 channels for one region and choose localized “show first channel only” or “show all configured channels” modes; internal enum names are not user-visible.
- [ ] Make the test view run all configured channels side by side so users can choose future configuration; test clicks never alter the currently running overlay.
- [ ] Show latency and cost estimates as estimates, provider capabilities, context data sent, worst-case request count, per-minute/daily budget and exact failure behavior.
- [ ] Define fixed-slot states for waiting, streaming, success, fallback, timeout, failure and cancellation; failed slots never collapse, and fallback provenance remains visible.
- [ ] Test channel reordering, provider removal, invalid REST configuration, offline incompatibility, multiple failures, and serialization round trips.
- [ ] Commit with `feat(ui): add deterministic multi-translator channels`.

## Task 8: Build glossary and style prompt management

**Files:**
- Create: `src/InfiniTranseon.App/Features/Glossary/GlossaryPage.xaml`
- Create: `src/InfiniTranseon.App/Features/Glossary/GlossaryViewModel.cs`
- Create: `src/InfiniTranseon.App/Features/Glossary/GlossaryEntryEditor.xaml`
- Create: `src/InfiniTranseon.App/Features/Glossary/StylePromptEditor.xaml`
- Test: `tests/InfiniTranseon.App.Tests/Glossary/GlossaryTests.cs`

**Interfaces:**
- Consumes: versioned glossary documents, style prompt templates with version identifiers, and provider glossary capability flags from `ProviderCapability`.
- Produces: glossary CRUD with explicit scope (profile, language pair), versioned import/export, and style prompt edits that invalidate translation caches through the existing glossary-version and prompt-version cache-key fields.

- [ ] Implement glossary entry editing with source term, target term, case sensitivity, protected-term flag (never auto-corrected by `ConservativeCorrectionService`), scope and notes; support search, sort, duplicate detection and bulk import/export in a versioned format.
- [ ] Show which configured providers apply the glossary natively and which fall back to placeholder protection or prompt injection, driven by capability data rather than hardcoded provider lists.
- [ ] Implement per-profile style prompt editing with named versions, a preview against the default template, and explicit apply semantics: saving a new version bumps the prompt version key so caches invalidate, and running targets pick it up only through the standard `Applying/Applied` acknowledgement flow.
- [ ] Warn when a glossary or prompt edit will invalidate cached translations, showing the affected profile scope; never silently retranslate running targets.
- [ ] Test 10,000-entry glossaries with virtualization, CJK terms, import conflicts, undo of destructive edits, and keyboard-only editing.
- [ ] Commit with `feat(ui): add glossary and style prompt management`.

## Task 9: Implement overlay style and multi-result layout preview

**Files:**
- Create: `src/InfiniTranseon.App/Features/OverlayStyles/OverlayStyleEditor.xaml`
- Create: `src/InfiniTranseon.App/Features/OverlayStyles/OverlayStyleViewModel.cs`
- Create: `src/InfiniTranseon.App/Features/OverlayStyles/OverlayPreview.xaml`
- Test: `tests/InfiniTranseon.App.Tests/OverlayStyles/OverlayStyleTests.cs`

**Interfaces:**
- Consumes/produces: `OverlayStyleDefinition`, `TextLayoutPolicy`, `MultiResultLayout`.
- Preview consumes Task 1 fake-rendered representative frames during UI development and real backend-rendered frames only after backend Task 11; it never persists them.

- [ ] Support complete replacement, translucent/blurred background, offset region, and floating/fixed panel modes per region. A floating panel is fixed, hotkey-triggered, or passively triggered by EngineHost cursor-position hit testing; it never receives pointer input.
- [ ] Support user-selected colors, opacity, blur, temporal background cache, automatic contrast, font, outline, padding, labels, spacing, and style presets.
- [ ] Preview the product maximum of 4 translator slots with short, average, long, CJK, RTL and mixed-direction text; isolate BiDi direction per label/result, reserve stable slots, and show waiting/fallback/failure plus overflow policies.
- [ ] Clearly state that current-frame pixels cannot reveal the true background behind text and preview the selected fallback.
- [ ] Remove decorative game-overlay animation; honor reduced motion in editor-only transitions.
- [ ] Warn below 4.5:1 normal-text or 3:1 large-text contrast, always provide outline/opaque/high-contrast fallbacks, and test maximum lines, 4K scaling, long provider labels, dynamic backgrounds and preset import/export.
- [ ] Commit with `feat(ui): add overlay and multi-result style editor`.

## Task 10: Add immersion-safe runtime controls, tray UI, and hotkeys

**Files:**
- Create: `src/InfiniTranseon.App/Features/RuntimeControls/TrayController.cs`
- Create: `src/InfiniTranseon.App/Features/RuntimeControls/RuntimeFlyout.xaml`
- Create: `src/InfiniTranseon.App/Features/RuntimeControls/HotkeyService.cs`
- Create: `src/InfiniTranseon.App/Features/RuntimeControls/OverlayStatusIntentService.cs`
- Create: `src/InfiniTranseon.App/Features/RuntimeControls/RecentTranslationsPanel.xaml`
- Create: `src/InfiniTranseon.App/Features/RuntimeControls/RecentTranslationsViewModel.cs`
- Test: `tests/InfiniTranseon.App.Tests/RuntimeControls/RuntimeControlTests.cs`
- Test: `tests/InfiniTranseon.App.UiTests/Immersion/FocusPreservationTests.cs`

**Interfaces:**
- Produces commands: pause/resume all, pause target, toggle overlay, manual OCR, switch translator group, retranslate visible generations, open recent translations, open workbench, exit. Every runtime binding has `AllRunning`, `ForegroundMatched` or explicit `TargetSet` scope.
- Consumes: `RuntimeHealthSummary`, target-level state, and read-only profile-scoped `RecentTranslationBuffer` snapshots. The panel must not require persistent history.

- [ ] Keep the process alive in a Win32 `Shell_NotifyIcon` tray integration after closing settings and expose one-layer emergency actions; use a native menu if the platform spike shows a XAML flyout activates the game.
- [ ] Register configurable global hotkeys, detect conflicts before save and at runtime, display actual registration state, recover after Explorer/permission/layout changes, permit disabling every hotkey, and provide a restore-defaults command.
- [ ] Implement translator-group switching that does not open a window, automatically retranslates currently visible text, and asks EngineHost to render only a short auto-dismissing non-interactive corner confirmation on the target display.
- [ ] Define one error severity matrix covering log-only, tray badge, game-corner indication, deduplication, rate limits, recovery and user actions; individual features cannot invent their own meaning of “critical.”
- [ ] Add a user-invoked accessible Recent Translations panel opened by tray or hotkey, with original, translation, channel, time, copy action and Narrator reading order; opening it explicitly activates the control app. When history is off it reads only the bounded in-memory session buffer, which clears on profile stop.
- [ ] Test that non-panel runtime actions preserve foreground HWND, keyboard focus and mouse capture, never open a modal, work with two targets, and survive Explorer/tray and EngineHost restart.
- [ ] Commit with `feat(ui): add immersion-safe tray and hotkeys`.

## Task 11: Implement profile-scoped History and translator comparison

**Files:**
- Create: `src/InfiniTranseon.App/Features/History/HistoryPage.xaml`
- Create: `src/InfiniTranseon.App/Features/History/HistoryViewModel.cs`
- Create: `src/InfiniTranseon.App/Features/History/TranslationEventDetail.xaml`
- Create: `src/InfiniTranseon.App/Features/History/CorrectionScopeDialog.xaml`
- Test: `tests/InfiniTranseon.App.Tests/History/HistoryTests.cs`

**Interfaces:**
- Consumes: paged `TranslationEventGroup` containing source, OCR metadata, ordered channel results, refinement provenance, latency, estimated cost, and cache status.
- Produces: search/filter, copy, export selected, save correction with explicit scope, and rerun translation commands.

- [ ] Group all channel results for one source generation; never present a history click as selecting the result currently shown in-game.
- [ ] Virtualize lists, preserve filters, support profile/target/region/provider/date filters, and include empty/retention-disabled states.
- [ ] Make history and persistent translation memory separate opt-ins per profile; when history is enabled default to text-only 30 days and 500 MB, both user-editable, and show estimated event count/disk use.
- [ ] Require scope selection for corrections: exact text, profile, region, language pair, and optional glossary version; make saved corrections undoable. History export is a separate text-bearing action from profile export and must show a privacy summary before writing.
- [ ] Test 100,000 events, long text, history disabled, retention cleanup, deleted provider metadata, and locale-aware dates/costs.
- [ ] Commit with `feat(ui): add profile history and translator comparison`.

## Task 12: Build Settings, provider setup, local models, and profile portability

**Files:**
- Create: `src/InfiniTranseon.App/Features/Settings/SettingsPage.xaml`
- Create: `src/InfiniTranseon.App/Features/Settings/ProviderSettingsPage.xaml`
- Create: `src/InfiniTranseon.App/Features/Settings/RestAdapterEditor.xaml`
- Create: `src/InfiniTranseon.App/Features/Settings/LocalModelsPage.xaml`
- Create: `src/InfiniTranseon.App/Features/Settings/ProfileImportReview.xaml`
- Test: `tests/InfiniTranseon.App.Tests/Settings/SettingsTests.cs`

**Interfaces:**
- Consumes: built-in adapter descriptors, OpenAI-compatible descriptor, declarative REST schema, secret references, model catalog, update status, `RuntimeCapabilities`, and `RuntimeBudgetSnapshot`.
- Produces settings and explicit install/download/remove requests; never receives raw stored secret values after save.

- [ ] Separate Appearance, Translation Services, OCR Services, Local Models, Privacy & History, Performance, Hotkeys, Updates, and About.
- [ ] Support built-in China/US providers, OpenAI-compatible endpoints, and configurable REST requests without code execution.
- [ ] Build API-key entry with paste allowed, a keyboard-accessible Toggle-pattern “show key” control, connection test, clear credential and inline localized error summaries. Re-hide on timeout, focus loss or navigation; show redacted raw provider errors only in an explicitly unlocalized technical-details section.
- [ ] Show local models as not installed until the user explicitly requests download; display license, size, language pair, checksum, disk location, and removal action.
- [ ] Review imported profile contents before save and enumerate excluded secrets, history, screenshots, models, personal paths, and unresolved target bindings.
- [ ] In import review, retain targets/regions over current limits as disabled items with localized hard-limit or dynamic-budget reasons. Test more than 8 targets, more than 256 regions per target and capability revision after EngineHost reconnect.
- [ ] Test HTTP warnings, secret redaction, offline mode, canceling model download, checksum failure, disk full, profile version migration, and signed update confirmation.
- [ ] Commit with `feat(ui): add services models privacy and update settings`.

## Task 13: Implement Diagnostics and visible degradation governance

**Files:**
- Create: `src/InfiniTranseon.App/Features/Diagnostics/DiagnosticsPage.xaml`
- Create: `src/InfiniTranseon.App/Features/Diagnostics/DiagnosticsViewModel.cs`
- Create: `src/InfiniTranseon.App/Features/Diagnostics/DegradationEventCard.xaml`
- Create: `src/InfiniTranseon.App/Features/Diagnostics/CrashReportExport.xaml`
- Test: `tests/InfiniTranseon.App.Tests/Diagnostics/DiagnosticsTests.cs`

**Interfaces:**
- Consumes: health snapshots, structured status events, degradation lifecycle events, redacted logs, local crash-report metadata.
- Produces: retry, open affected setting, lock/unlock region degradation, export redacted diagnostic package.

- [ ] Show each failure with affected profile/target/region, current behavior, root error, timestamp, and direct recovery action.
- [ ] Show each degradation with previous/new setting, reason, recovery condition, and whether the region is user-locked.
- [ ] Never imply fallback occurred unless that fallback was explicitly configured and confirmed by runtime state.
- [ ] Build local crash-report review/export; no upload button or hidden telemetry endpoint exists.
- [ ] Add deduplicated, rate-limited live-region announcements in the control app: routine updates are polite, only actionable critical failures are assertive, and recovery events identify what recovered. Send no screen-reader output to the click-through game overlay.
- [ ] Test event storms, disconnected engine, corrupted log segment, redaction, recovery transitions, and “locked region settings are never auto-modified; impossible capacity pauses/errors explicitly without backlog.”
- [ ] Commit with `feat(ui): add diagnostics and degradation visibility`.

## Task 14: Complete frontend quality and release gates

**Files:**
- Create: `tests/InfiniTranseon.App.UiTests/Flows/FirstRunFlowTests.cs`
- Create: `tests/InfiniTranseon.App.UiTests/Flows/MultiWindowFlowTests.cs`
- Create: `tests/InfiniTranseon.App.UiTests/Flows/OfflinePrivacyFlowTests.cs`
- Create: `tests/InfiniTranseon.App.UiTests/Immersion/NoInterruptionTests.cs`
- Create: `tests/InfiniTranseon.App.Tests/State/GenerationOrderingTests.cs`
- Create: `docs/testing/frontend-release-checklist.md`

**Interfaces:**
- Consumes the backend fake services and deterministic target simulator defined by the backend plan.
- Produces CI evidence for accessibility, localization, focus preservation, profile flows, and runtime-state recovery.

- [ ] Add end-to-end tests for first run, multi-target setup, multi-translator fixed slots, translator hotkey switching, history comparison, offline mode, and profile export/import.
- [ ] Add deterministic RuntimeStateStore tests for out-of-order streaming chunks, old results after translator-group switch, target close, profile revision and EngineHost restart; accept source events only for the current SourceGenerationToken and translation/stream updates only for the complete current channel/stage execution hierarchy.
- [ ] Run accessibility scans for keyboard-only, screen reader names/patterns, Recent Translations reading order, high contrast, reduced motion, and 200% text scaling.
- [ ] Test the Mica path and the solid-material fallback (transparency disabled, remote session) at 100%, 150%, 200%, and 300% DPI.
- [ ] Measure profile-center launch, workbench interaction, history virtualization, and state-update latency; block release on UI hangs over 100 ms on the UI thread.
- [ ] Split normal unit/integration CI from interactive self-hosted Windows UI jobs. Run `dotnet test InfiniTranseon.sln -c Release` for deterministic tests, and separately execute Windows 11 DWM, DPI, focus, Narrator/Accessibility Insights and multi-monitor release gates.
- [ ] Commit with `test(ui): enforce accessibility and immersion release gates`.

## Frontend definition of done

- A new user can reach a working translation overlay without opening the advanced workbench.
- An expert can configure arbitrary regions, priorities, intervals, OCR, multiple translation channels, glossaries, style prompts, line-break policies, overlay strategies, and degradation locks.
- A running game never loses focus during normal translation, service failure, translator switching, or performance degradation.
- No game overlay asks the user to select a translation result.
- Up to 4 translator results can appear simultaneously in stable labeled stateful slots and remain comparable in history.
- Assistive-technology users can open a complete Recent Translations panel without depending on the click-through overlay.
- All user-visible errors provide a recovery action; no silent fallback or fake success state exists.
- The entire flow works in English and Simplified Chinese, keyboard-only, high contrast, reduced motion, and at 200% text scaling.
