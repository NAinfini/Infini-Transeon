---
name: Infini-Transeon
description: A quiet, native Windows control surface for configuring and running game translation.
colors:
  information-light: "#005FB8"
  success-light: "#0F7B0F"
  warning-light: "#9D5D00"
  critical-light: "#C42B1C"
  information-dark: "#60CDFF"
  success-dark: "#6CCB5F"
  warning-dark: "#FCE100"
  critical-dark: "#FF99A4"
  preview-light: "#DDDDE1"
  preview-dark: "#23252E"
typography:
  title:
    fontFamily: "Segoe UI Variable, Segoe UI, sans-serif"
    fontSize: "28px"
    fontWeight: 600
    lineHeight: 1.2
  section:
    fontFamily: "Segoe UI Variable, Segoe UI, sans-serif"
    fontSize: "20px"
    fontWeight: 600
    lineHeight: 1.3
  body:
    fontFamily: "Segoe UI Variable, Segoe UI, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.4
  caption:
    fontFamily: "Segoe UI Variable, Segoe UI, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.35
  metric:
    fontFamily: "Cascadia Mono, Consolas, monospace"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.3
rounded:
  control: "4px"
  card: "8px"
  badge: "10px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "16px"
  xl: "24px"
  xxl: "32px"
components:
  card:
    backgroundColor: "Windows Fluent CardBackgroundFillColorDefaultBrush"
    rounded: "{rounded.card}"
    padding: "{spacing.lg}"
  badge:
    rounded: "{rounded.badge}"
    padding: "2px 8px"
  setting-row:
    backgroundColor: "Windows Fluent CardBackgroundFillColorDefaultBrush"
    rounded: "{rounded.card}"
    padding: "12px 16px"
---

# Design System: Infini-Transeon

## Overview

**Creative North Star: "The Quiet Mission Console"**

Infini-Transeon should feel like a native Windows control surface that has already done the hard thinking. Its visual system is restrained, information-dense where configuration demands it, and nearly invisible during play. It refuses the category habit of neon gaming chrome, decorative glass, and equal-weight dashboard cards.

The interface uses Windows Fluent materials and controls as behavior, not decoration. Identity comes from disciplined state communication, profile-owned workspaces, live capture previews, stable translation slots, and precise spacing. The application supports light, dark, and high-contrast themes without a separate visual vocabulary.

**Key Characteristics:**

- Native Fluent structure with calm, low-noise surfaces.
- Profile-first hierarchy: Home launches; workspaces configure; tray controls play.
- Accent is rare and action-oriented; semantic colors communicate real state.
- Real previews and operational status replace decorative metrics.
- Advanced controls use progressive disclosure without hiding essential recovery.

## Colors

Page surfaces and controls use WinUI theme resources so the system accent, light/dark theme, transparency setting, remote-session fallback, and high contrast remain authoritative. Fixed colors are limited to semantic status and controlled preview surfaces defined in `Theme/DesignTokens.xaml`.

**The System Owns the Accent Rule.** Pages never bind directly to low-level system accent or card brushes; they consume the application aliases `AccentDefault`, `AccentText`, `SurfaceBackground`, `SurfaceCard`, `SurfaceCardHover`, and `SurfaceSunken`.

**The State Has Three Channels Rule.** Success, warning, critical, informational, and neutral state always combine icon, localized text, and semantic color.

## Typography

**Display and Body Font:** Segoe UI Variable with Segoe UI fallback  
**Metric Font:** Cascadia Mono, only for measurements, timings, coordinates, and identifiers

The hierarchy is compact and native: 28px page titles, 20px section headings, 16px emphasized body, 14px body, and 12px auxiliary text. Weight and spacing establish hierarchy; all-caps labels and decorative monospace are excluded.

## Layout

The default window is 1280×800 with a hard minimum of 960×600. Content is centered up to 1200 epx; forms stop at 820 epx. Layout states are Wide at 1120+, Compact at 820–1119, and Narrow below 820. Profile cards have a stable 320 epx working width, while dense editors allocate 220 epx to workspace navigation and up to 360 epx to an inspector.

Spacing uses a 4/8/12/16/24/32 epx scale. Related controls remain tight; sections receive more space above than their labels receive below. Narrow layouts stack functional regions in task order instead of merely shrinking columns.

## Elevation & Depth

Depth is structural. Cards use Fluent tonal surfaces and strokes at rest; dialogs and transient teaching surfaces use native elevation. Blur or Mica is allowed only when Windows composition provides it and must fall back to an opaque theme surface when transparency is unavailable.

## Shapes

Controls use the native 4 epx radius, working cards use 8 epx, and compact status badges use a 10 epx capsule radius. Large pill-shaped containers, decorative halos, and nested rounded cards are not part of the system. Selected rows use a theme surface plus a narrow accent indicator and never rely on color alone.

## Components

- **PageShell:** Owns title, subtitle, command region, loading, error, empty, and content states. A page cannot silently omit operational state.
- **ProfileCard:** Shows a real or explicitly unavailable thumbnail, readiness, language direction, region/channel counts, and one clear start-or-repair action.
- **RunningTargetBar:** Prioritizes pause, overlay visibility, manual recognition, stop, and workspace entry without opening a modal.
- **StatusBadge:** Always displays a glyph and localized label with an accessible combined name.
- **StickySaveBar:** Appears only for dirty workspace state and moves through Unsaved → Applying → Applied or Rolled back.
- **RegionCanvas and RegionListPane:** Present the same geometry through pointer and keyboard-accessible paths.
- **ChannelPipelineCard:** Shows initial translation, zero to two refinements, fallback behavior, cost/latency implications, and stable slot order inline.
- **Dialogs:** Reserved for protected focus, privacy consent, credentials, or destructive confirmation; ordinary editing remains inline.

## Do's and Don'ts

### Do:

- **Do** use application token aliases and native WinUI behavior across every theme.
- **Do** keep primary runtime actions visible and recovery actions adjacent to the failure.
- **Do** show disabled reasons, pending state, empty state, and real data provenance.
- **Do** preserve keyboard equivalents for every canvas operation.
- **Do** localize complete phrases rather than concatenating translated fragments.

### Don't:

- **Don't** use neon gaming styling, decorative gradients, glass panels, or blur without a functional reason.
- **Don't** build pages from equal-sized metric cards or nest cards to manufacture hierarchy.
- **Don't** expose target-, region-, and channel-level settings in one undifferentiated inspector.
- **Don't** present static examples as live readiness or make a control look operational without behavior.
- **Don't** interrupt gameplay with result selection, modal errors, notifications, or focus-stealing windows.
