# Design

## Visual Theme

Compact dark system utility modeled closely on OpenUsage's neutral charcoal popover. The dashboard is a calm surface on a dim desktop, with provider accents reserved for progress, ring slices, and status marks.

## Color Palette

- Canvas: deep blue-charcoal, never pure black.
- Panel: slightly raised charcoal for spend and provider surfaces.
- Text: warm near-white for primary values, cool gray for context, muted gray for labels.
- Accents: provider-specific teal, terracotta, indigo, blue, and amber/red state colors.
- Avoid gradients and saturated decoration outside data state.

## Typography

Segoe UI with semibold values, compact uppercase section labels, and monospaced digits for percentages, costs, and countdowns.

## Components

Rounded spend card with segmented period control and ring legend; provider header plus inset metric card; full-width capsule quota meters; compact trend bars; dark options menu; borderless rounded settings and customization screens; native tray icon and supported taskbar button with a live quota badge.

## Layout

The dashboard uses a narrow, vertically scrollable popover-like composition. Header chrome stays fixed, the spend summary leads the provider groups, and each provider owns an inset card. Always-visible metrics stay above a centered On Demand disclosure.

## Motion

Use short ease-out entrance and navigation transitions, numeric content transitions for changing values, and smooth bar/ring interpolation. Respect reduced motion by shortening or disabling nonessential transitions.
