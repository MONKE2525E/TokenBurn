# TokenBurn architecture

TokenBurn is a Windows-first .NET 10 application with a small Rust/Tauri presentation host. The .NET process owns provider access, local state, the loopback API, settings, the tray, and the reliable native taskbar boundary. Tauri owns the compact popup presentation when its published binary is available.

## Runtime flow

1. `UsageMonitor.Desktop` starts the single-instance WPF application.
2. The desktop shell creates the provider catalog, cache, secret store, diagnostics logger, tray, dashboard, and taskbar surface.
3. Providers read supported local files, read-only SQLite databases, or provider endpoints using credentials already present in local OS stores.
4. Provider adapters map raw responses into provider-neutral `ProviderSnapshot`, `MetricLine`, and usage-history models.
5. The stale-while-revalidate cache keeps the UI responsive and preserves last-good values when a refresh fails.
6. The desktop dashboard, taskbar surface, CLI, and loopback API consume the same normalized snapshots.
7. The optional Tauri popup calls the loopback contract and sends limited control messages back to the WPF process.

## Important paths

| Area | Paths |
| --- | --- |
| Normalized models and provider contracts | `UsageMonitor.Core/Models.cs`, `Contracts.cs` |
| Provider registration | `UsageMonitor.Core/ProviderCatalog.cs` |
| Provider readers and mappers | `UsageMonitor.Core/Providers/` |
| Local paths, cache, secrets, and redaction | `UsageMonitor.Core/UsageMonitorPaths.cs`, `JsonFileUsageCache.cs`, `SecureStorage.cs`, `Diagnostics.cs` |
| Loopback API | `UsageMonitor.LocalApi/` |
| Desktop shell and settings | `UsageMonitor.Desktop/` |
| Native taskbar and tray integration | `UsageMonitor.Desktop/NativeMethods.cs`, `Taskbar*.cs`, `Tray*.cs` |
| Tauri popup | `UsageMonitor.TauriPoc/dist/`, `UsageMonitor.TauriPoc/src-tauri/` |
| Test contracts and fixtures | `UsageMonitor.Tests/` |

## Boundaries that matter

- The local API binds to loopback. It is not a remote service and must not be changed to listen on all interfaces casually.
- Providers must return truthful unavailable, authentication, rate-limit, parse, or unsupported states. They must not convert a missing source into zero usage.
- JSONL history readers stream lines. Do not replace them with unbounded whole-file reads.
- OpenCode and Codex SQLite readers are read-only. TokenBurn must never migrate or write to provider-owned databases.
- Secrets stay in Windows Credential Manager or DPAPI-backed storage. Normalized snapshots and diagnostics contain redacted metadata only.
- The WPF process remains the owner of Explorer-facing taskbar behavior. A Tauri rewrite must not remove the native fallback.
- Settings migrations must preserve old values and keep defaults safe when fields are missing or invalid.
