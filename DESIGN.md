# TokenBurn Design System

Source of truth: [`UsageMonitor.TauriPoc/dist/styles.css`](UsageMonitor.TauriPoc/dist/styles.css).

Compact dark system utility modeled closely on OpenUsage's neutral charcoal popover. The dashboard is a calm surface on a dim desktop, with provider accents reserved for progress, ring slices, and status marks.

## Visual Theme & Palette

Dark-first system palette with semi-translucent frosted layers:

| Token | Value | Role |
|---|---|---|
| `--canvas` | `#141519` | Base dark canvas background |
| `--frost-surface` | `linear-gradient(145deg, rgba(27,28,32,.995), rgba(16,17,20,.99))` | Primary popover card fill |
| `--frost-chrome` | `linear-gradient(180deg, rgba(31,32,36,.993), rgba(21,22,25,.99))` | Fixed header & top chrome |
| `--frost-menu` | `rgba(35, 36, 40, .995)` | Floating dropdown and context menu fill |
| `--panel` | `rgba(39, 40, 44, .987)` | Provider cards and summary panels |
| `--panel-soft` | `rgba(46, 47, 51, .982)` | Inset metric backgrounds and badge fills |
| `--track` | `#3a3a3d` | Unfilled meter and slider track background |
| `--text` | `#f1f1f2` | Primary text and headlines |
| `--muted` | `#a4a4a8` | Secondary labels, descriptions, and timestamps |
| `--blue` | `#0797f6` | Default focus ring, Antigravity / Gemini accent |
| `--teal` | `#18aa90` | Codex / ChatGPT active quota accent |
| `--orange` | `#F8293D` | Claude Code / Anthropic accent, error thresholds |
| `--warning` | `#f4b74d` | Reset soon / approaching quota limit state |

## Typography

- **Font Family:** `-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`.
- **Numbers & Counters:** Monospaced digits (`font-variant-numeric: tabular-nums`) for currency amounts, token metrics, reset countdown timers, and percentage bars.
- **Focus Rings:** All interactive controls receive `:focus-visible { outline: 2px solid var(--blue); outline-offset: 2px; border-radius: 8px; }`.

## Motion Scale & Transitions

Transitions use a unified rhythm with decelerating entrances and accelerating exits:

| Token | Duration | Purpose |
|---|---|---|
| `--dur-micro` | `110ms` | Hover, active press, color state updates |
| `--dur-base` | `180ms` | Panels, toggles, meters, overlays |
| `--dur-page` | `260ms` | Page-view navigation and drill-downs |
| `--dur-color` | `260ms` | Quota threshold crossings (slower than bar animation) |

- **Easings:**
  - `--ease-out`: `cubic-bezier(.2, .8, .2, 1)` (standard deceleration)
  - `--ease-out-quint`: `cubic-bezier(.22, 1, .36, 1)` (smooth page entrances)
  - `--ease-in`: `cubic-bezier(.4, 0, 1, 1)` (snappy exits)

## Floating Layers & Overlays

Floating menus and modals use the `.overlay-surface` pattern rather than `@starting-style` for compatibility across all WebView2 runtimes:
- Inactive: `opacity: 0; visibility: hidden; transform: translateY(-4px) scale(.97);`
- Open: `.overlay-surface.is-open { opacity: 1; visibility: visible; transform: none; }`

## Native Windows Taskbar Strip

Managed by `UsageMonitor.Desktop` (.NET 10 / WPF host):
- Rendered offscreen as a bitmap and displayed in a native layered HWND.
- Height: 24–30px, displaying real-time quota percentage or active cost indicator.
