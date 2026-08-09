# TokenBurn Tauri presentation host

This is the Tauri presentation layer used by the Windows shell. The .NET solution remains the
provider, cache, credential, API, and Explorer integration owner.

- The Rust/Tauri shell owns the frameless, transparent popup. In hosted mode it has no tray icon;
  the WPF shell keeps the single Windows tray icon and sends popup coordinates over loopback.
- The existing `UsageMonitor.Desktop` process owns the .NET providers, cache, credential handling,
  and loopback API at `http://127.0.0.1:6736`.
- The native Win32/WPF taskbar strip remains the shell boundary. Tauri owns the popup presentation,
  while the supported taskbar button and tray stay in .NET so Explorer restarts cannot take down the
  usage surface.
- The popup is deliberately compact: 320 logical pixels wide by 800 logical pixels high, matching
  the dimensions used by the upstream SwiftUI panel. It is a popup surface, not a normal desktop
  window, and is clamped to the monitor work area at the selected DPI.
- The tray glyph is drawn as a small three-bar usage mark instead of reusing the earlier ring asset,
  which stayed muddy at notification-area sizes.
- The popup is focusable but has no taskbar button. Escape and native focus loss both dismiss it.
- Spend is calculated from the existing local history. Claude and Codex history remains visible when
  a live provider endpoint is unavailable, with an explicit warning instead of fabricated zeros.

## Run

1. Start the existing desktop build so the loopback API is listening.
2. From this folder run `npm install` once, then `npm run dev`.
3. Click the tray icon. The popup is positioned from the tray click coordinates, has no normal
   application button, and hides on Escape or focus loss.

The Anthropic usage endpoint can rate-limit aggressively. In that case the POC deliberately shows the
provider warning and whatever cached local history is available. It never invents zero usage.

For a deterministic screenshot of the compact surface during development, set
`USAGE_MONITOR_POC_REVEAL=1` before launching the debug executable. This is only a test hook and is
not part of the normal tray workflow.

The migration is intentionally hybrid. The WPF process owns the native Explorer taskbar strip and
the provider/cache/API process boundary. The Tauri process owns the popup surface, launched with
`--hosted` beside the published desktop executable. Its control channel is restricted to
`127.0.0.1:6737` and carries only screen coordinates and show/hide commands. While the WPF shell
is running, its hosted popup stays resident after it is hidden so tray and taskbar activation
remains warm; it is stopped only when the shell exits. A separate desktop
control channel on `127.0.0.1:6738` lets the Tauri Options button open the existing WPF settings
dialog without exposing provider data to the shell.
