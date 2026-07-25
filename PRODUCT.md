# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows

## Users

Windows 11 players who need untranslated game text recognized and translated without repeatedly leaving the game. The product must support both first-time users who want a short setup path and advanced users who want precise control over capture regions, OCR, translation chains, overlays, performance, and per-game profiles.

## Product Purpose

Infini-Transeon captures a selected window, display, or desktop region; recognizes game text; translates it through user-configured online services or optional local models; and presents the result over or near the original text. Success means the user configures a profile once, starts it quickly, and can keep playing without modal interruptions or focus theft.

## Positioning

The differentiating mechanism is a profile-owned, region-aware translation pipeline: each region controls its own recognition cadence, priority, layout, overlay strategy, and one to four stable translation-result slots, while the runtime reuses OCR and translation work to protect latency and cost.

## Operating Context

- Windows 11 x64, API baseline build 22621; windowed and borderless-fullscreen capture.
- Multiple capture targets may be active, while the first-release UI presents one runtime session and preserves a multi-session-ready data model.
- Users may configure full-window scanning, one or more normalized regions, or both.
- In-game interaction happens through overlays, global hotkeys, and the notification-area menu; the main window is primarily for setup, maintenance, history, and diagnostics.
- Translation credentials are supplied by the user. China- and US-accessible providers, OpenAI-compatible endpoints, and configurable REST adapters are first-class.

## Capabilities and Constraints

- Game profiles own targets, regions, OCR behavior, translation channels, overlay behavior, context, glossary, history policy, and performance policy.
- Each region supports one to four parallel translation channels. A channel has one initial translator and up to two optional LLM refinement steps. In-game results remain in stable slots; users do not select a winning sentence while playing.
- OCR and translation caches must avoid repeated work. Context may include game name, description, previous accepted context, region role, and glossary data.
- Local models are never bundled or downloaded automatically. The application presents a signed catalog and downloads a selected model only after an explicit user action.
- Overlay modes include full replacement, translucent or blurred backing, offset translation, and hover-panel presentation. The user controls the strategy per region.
- All degradation is visible and logged. A region may be locked against automatic degradation.
- Profiles support versioned import and export while excluding secrets, history, screenshots, models, and personal paths.
- The application is open source under Apache-2.0. GitHub distributes unsigned installer and portable builds until Authenticode becomes available.

## Brand Commitments

The product name is Infini-Transeon. Product language is calm, direct, and technical without being cryptic. The approved interface direction is native, restrained Windows Fluent: a dependable game-side control surface rather than a flashy gaming overlay or developer dashboard. English and Simplified Chinese are equally supported application languages.

## Evidence on Hand

- Product and immersion rules: `docs/product/2026-07-19-product-ux-architecture-review.md`
- Approved redesign decisions: `docs/design/2026-07-24-redesign/00-decisions.md`
- Page and workflow specifications: `docs/design/2026-07-24-redesign/05-page-specs.md`
- Runtime architecture: `docs/superpowers/specs/2026-07-19-runtime-architecture-design.md`
- Existing WinUI implementation, tests, and resource dictionaries under `src/InfiniTranseon.App` and `tests/InfiniTranseon.App.Tests`

No customer testimonials, usage benchmarks, commercial claims, or external brand assets have been supplied; future work must not fabricate them.

## Product Principles

1. Preserve immersion: no focus theft, modal result picking, or noisy application chrome during play.
2. Make the common path short and the advanced path deep.
3. Put configuration where it belongs: global defaults at application level, overrides at profile, target, region, or channel level.
4. Show real readiness, pending work, degradation, and recovery actions; never imply success through a static mock.
5. Spend OCR, network, compute, and screen space deliberately.

## Accessibility & Inclusion

All interactive controls require keyboard access and an accessible name. State must use icon, text, and color. The application must remain usable with Windows high contrast, reduced motion, 200% text scaling, and both supported language packs. The minimum window is 960×600, with layouts validated at 1280×720 and above.
