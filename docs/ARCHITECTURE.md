# TokenBurn architecture

TokenBurn is a Windows-first application with a split runtime. The normal visible dashboard is a
Rust/Tauri process hosting the WebView frontend. The .NET 10 process is still the long-lived
Windows host and backend: it collects provider data, owns local state and the loopback API, and
implements the Windows shell surfaces that need native Explorer and Win32 behavior.

`UsageMonitor.TauriPoc` is a historical directory name. It now contains the packaged Tauri
presentation host, not an optional proof of concept. The executable produced from it is named
`tokenburn-desktop.exe` and is shipped beside `TokenBurn.exe`.

## Runtime flow

1. `TokenBurn.exe`, built from `UsageMonitor.Desktop`, starts the single-instance WPF application.
2. The .NET host creates `MainWindow` and initializes the provider catalog, cache, secret store,
   refresh loop, loopback API, tray service, and taskbar overlay. `MainWindow` is immediately
   hidden in the normal hosted configuration. Its code still coordinates the backend and retains
   legacy WPF presentation paths for compatibility.
3. The host starts the sibling `tokenburn-desktop.exe` process with `--hosted`. Its `main` window
   is a frameless Tauri/WebView popup and is initially hidden until the user opens TokenBurn.
4. A normal launch, tray click, taskbar-strip click, notification activation, or jump-list Open
   action asks the Tauri process to show the popup at the relevant screen position.
5. The Tauri WebView renders the compact dashboard, provider cards, detailed usage/breakdown view,
   and the in-popup Settings and Customize pages. The Rust side owns the popup HWND, placement,
   focus, dismissal, sizing, and screen-share privacy for that window.
6. The .NET refresh loop reads provider sources and normalizes the results. Each provider has a
   bounded read, and the desktop host also bounds the complete refresh batch so a stuck cache or
   coordination task cannot leave the UI loading forever. Failed batches keep last-good values,
   write a correlated diagnostic, and schedule an automatic retry. The host updates the native
   taskbar strip and exposes the same redacted snapshot through the loopback API for the Tauri
   frontend and CLI.

The .NET host supervises the hosted popup child. If the child exits, the host retries it with a
short delay and a bounded per-minute restart budget. Tray and taskbar actions can make a fresh
start attempt after that budget, while the native shell surfaces and provider refresh loop remain
available throughout.

The tray keeps a single TokenBurn mark. The Tauri process only creates its own tray icon when it
is launched standalone (without `--hosted`). A standalone popup host that starts while the .NET
desktop host is already running defers to it — it posts the host's activation message and exits
before creating any tray icon — and a standalone instance that receives a forwarded `--hosted`
spawn yields and exits so the host can relaunch a properly hosted popup. The host also ends stray
popup-host processes left over from earlier runs during its own startup.

A `tauri dev` session overwrites the shared `target\debug` popup-host binary with a build whose
UI lives on the (short-lived) dev server. Such a build refuses a hosted role with exit code 7,
and the desktop host responds by blacklisting that file — until it is rebuilt — and spawning the
next-newest candidate instead, so a dead dev server can never render inside the hosted popup.

If the Tauri companion executable is missing, the .NET taskbar and tray surfaces can still run, but
the current normal dashboard cannot be shown through the legacy WPF window. The WPF dashboard is
not the supported runtime fallback.

## Presentation and native ownership

| Surface or responsibility | Current owner |
| --- | --- |
| Main dashboard and provider cards | Tauri `main` WebView window |
| Detailed usage, cost charts, provider/model breakdowns, and cache statistics | Tauri WebView |
| Settings and Customize reached from the dashboard, tray menu, or jump list | Tauri in-popup pages |
| Popup HWND, placement, focus, dismissal, resize, and screen-share privacy | Rust/Tauri process |
| Notification-area icon and tray menu | .NET `TrayIconService` using `NotifyIcon`, with a WPF tray menu and a WinForms fallback menu |
| Visible taskbar status strip HWND and Explorer interaction (auto-hides while another app is fullscreen on the strip's monitor) | .NET `NativeTaskbarOverlay` in `TaskbarOverlayController` |
| Taskbar strip visual | A WPF `WidgetWindow` rendered offscreen to a bitmap, then displayed by the native layered HWND |
| Provider collection, refresh scheduling, stale-while-revalidate behavior, pricing, and cache | .NET `UsageMonitor.Desktop` plus `UsageMonitor.Core` |
| Loopback usage API | .NET `UsageMonitor.LocalApi` on `127.0.0.1:6736` |
| Tauri-to-WPF control and settings bridge | Loopback control server on `127.0.0.1:6738` |
| WPF-to-Tauri show/toggle control | Tauri control server on `127.0.0.1:6737` |

`UsageMonitor.Desktop` still contains a WPF `MainWindow`, `SettingsDialog`, and `CustomizeDialog`,
but those are not the normal visible presentation path today. The application starts the WPF
window to establish its HWND and dispatcher, run the .NET coordination code, and support native
compatibility paths. The normal `--settings` jump-list path and tray menu open Tauri pages. A legacy
activation path can still invoke the native WPF settings dialog, so that code must not be removed
without checking the activation and fallback behavior.

## Data and process boundaries

The .NET side creates a `ProviderCatalog` containing the supported Codex, Claude Code, Antigravity,
and OpenCode providers. `CoreUsageSnapshotSource` reads local files, read-only provider databases,
and supported provider endpoints, then maps results into normalized usage snapshots. The cache,
pricing catalog, secret store, diagnostics redaction, and settings migrations remain on the .NET
side.

The Tauri frontend normally calls the Rust `fetch_usage` command. Rust requests JSON from the
loopback usage API at `http://127.0.0.1:6736/v1/usage`, and the frontend can use that same endpoint
directly during a WebView startup race. The Tauri commands for provider selection, refresh status,
settings data, settings writes, and spend metric changes call the .NET control server at
`127.0.0.1:6738`. The WPF host sends only popup commands and screen coordinates to the Tauri control
server at `127.0.0.1:6737`.

These services are loopback-only. They are local process boundaries, not a TokenBurn cloud service
or a remote API. All three loopback surfaces share one security gate: requests with a foreign
`Host` header are rejected (DNS-rebinding defense), and browser requests whose `Origin` is not an
exact allowlisted app origin are rejected outright, so a webpage can neither read the loopback
data (CORS reflects only allowlisted origins) nor trigger side effects such as forced refreshes or
settings writes. Same-user native processes send no Origin and remain trusted, because they can
already read the user's files; a process-secret scheme would not raise that bar.

Side-effectful routes (forced refresh, settings writes, popup show/hide) additionally require the
`X-TokenBurn-Client: 1` native-client marker header whenever the request has no Origin. This is
the defense against `<img>`/`<script>`/`<link>` GETs from a hostile webpage, which carry no Origin
at all: browsers cannot attach the marker without a CORS preflight, and the origin gate rejects
preflights from foreign origins. The marker is not a secret and not an authentication boundary
between same-user processes — it only separates browser-context requests from native clients.

## Important paths

| Area | Paths |
| --- | --- |
| Normalized models and provider contracts | `UsageMonitor.Core/Models.cs`, `Contracts.cs` |
| Provider registration | `UsageMonitor.Core/ProviderCatalog.cs` |
| Provider readers and mappers | `UsageMonitor.Core/Providers/` |
| Local paths, cache, secrets, and redaction | `UsageMonitor.Core/UsageMonitorPaths.cs`, `JsonFileUsageCache.cs`, `SecureStorage.cs`, `Diagnostics.cs` |
| Loopback API | `UsageMonitor.LocalApi/` |
| .NET host, refresh orchestration, settings bridge, tray, and native shell | `UsageMonitor.Desktop/` |
| Native taskbar strip | `UsageMonitor.Desktop/TaskbarOverlayController.cs`, `WidgetWindow.xaml.cs` |
| Tauri dashboard and settings pages | `UsageMonitor.TauriPoc/dist/index.html`, `dist/app.js` |
| Tauri window and IPC boundary | `UsageMonitor.TauriPoc/src-tauri/src/main.rs` |
| Tauri companion packaging | `UsageMonitor.TauriPoc/src-tauri/tauri.conf.json`, `UsageMonitor.Desktop/UsageMonitor.Desktop.csproj` |
| Test contracts and fixtures | `UsageMonitor.Tests/` |

## Boundaries that matter

- Keep the Tauri WebView as the normal dashboard presentation layer and keep the .NET native shell
  boundary intact. They solve different problems.
- The local API binds to loopback. It is not a remote service and must not be changed to listen on
  all interfaces casually.
- Providers must return truthful unavailable, authentication, rate-limit, parse, or unsupported
  states. They must not convert a missing source into zero usage.
- JSONL history readers stream lines. Do not replace them with unbounded whole-file reads.
- OpenCode and Codex SQLite readers are read-only. TokenBurn must never migrate or write to
  provider-owned databases.
- Secrets stay in Windows Credential Manager or DPAPI-backed storage. Normalized snapshots and
  diagnostics contain redacted metadata only.
- Settings migrations must preserve old values and keep defaults safe when fields are missing or
  invalid.
- The taskbar renderer may use WPF to produce a bitmap, but the visible taskbar hit-test and shell
  placement belong to the native overlay HWND. Do not confuse that small strip renderer with the
  main dashboard.
