#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde_json::Value;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::process::Command;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{mpsc, Mutex};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tauri::image::Image;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{
    AppHandle, Emitter, LogicalSize, Manager, PhysicalPosition, PhysicalSize, Position, State,
    WebviewWindow, WindowEvent,
};
use windows::core::{w, HSTRING};
use windows::Win32::Foundation::{GlobalFree, HANDLE, HGLOBAL};
use windows::Win32::System::DataExchange::{
    CloseClipboard, EmptyClipboard, OpenClipboard, RegisterClipboardFormatW, SetClipboardData,
};
use windows::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE};
use windows::Win32::System::Ole::{CF_DIB, CF_UNICODETEXT};
use windows::Win32::UI::Shell::SetCurrentProcessExplicitAppUserModelID;
use windows::Win32::UI::WindowsAndMessaging::{
    GetAncestor, SetWindowDisplayAffinity, SetWindowPos, ShowWindow, GA_ROOT, HWND_TOPMOST,
    SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SW_HIDE, SW_SHOW, WDA_EXCLUDEFROMCAPTURE, WDA_NONE,
};

const API_BASE: &str = "http://127.0.0.1:6736";
const CONTROL_BIND: &str = "127.0.0.1:6737";
// Keep the popup at a compact logical size. These are logical
// pixels, then Tauri applies the selected monitor's DPI scale at the native window boundary.
const POPUP_WIDTH: i32 = 320;
const BREAKDOWN_WIDTH: i32 = 920;
const POPUP_HEIGHT: i32 = 800;
const POPUP_SCREEN_MARGIN: f64 = 16.0;
static REVEALING: AtomicBool = AtomicBool::new(false);
static FOCUS_GENERATION: AtomicU64 = AtomicU64::new(0);
static POPUP_INTENT_GENERATION: AtomicU64 = AtomicU64::new(0);
static LAST_FOCUS_HIDE_MS: AtomicU64 = AtomicU64::new(0);
static BREAKDOWN_GEOMETRY_GENERATION: AtomicU64 = AtomicU64::new(0);
static BREAKDOWN_GEOMETRY_ANIMATING: AtomicBool = AtomicBool::new(false);
static POPUP_MOTION_GENERATION: AtomicU64 = AtomicU64::new(0);
static POPUP_MOTION_REDUCED: AtomicBool = AtomicBool::new(false);
static POPUP_ANCHOR_BOTTOM: AtomicBool = AtomicBool::new(true);

#[derive(Clone, Copy, Debug)]
struct PhysicalWindowBounds {
    x: i32,
    y: i32,
    width: i32,
    height: i32,
}

static COMPACT_BREAKDOWN_BOUNDS: Mutex<Option<PhysicalWindowBounds>> = Mutex::new(None);

fn animated_window_bounds(
    start: PhysicalWindowBounds,
    target: PhysicalWindowBounds,
    progress: f64,
    eased: f64,
    squash_px: f64,
    anchor_bottom: bool,
) -> PhysicalWindowBounds {
    let base_y = start.y as f64 + (target.y - start.y) as f64 * eased;
    let base_height = start.height as f64 + (target.height - start.height) as f64 * eased;
    let squash = (std::f64::consts::PI * progress).sin().max(0.0) * squash_px;
    PhysicalWindowBounds {
        x: (start.x as f64 + (target.x - start.x) as f64 * eased).round() as i32,
        y: (base_y + if anchor_bottom { squash } else { 0.0 }).round() as i32,
        width: (start.width as f64 + (target.width - start.width) as f64 * eased)
            .round()
            .max(1.0) as i32,
        height: (base_height - squash).round().max(1.0) as i32,
    }
}

fn apply_window_bounds(window: &WebviewWindow, bounds: PhysicalWindowBounds) -> Result<(), String> {
    if let Ok(hwnd) = window.hwnd() {
        unsafe {
            SetWindowPos(
                hwnd,
                Some(HWND_TOPMOST),
                bounds.x,
                bounds.y,
                bounds.width.max(1),
                bounds.height.max(1),
                SWP_NOACTIVATE,
            )
            .map_err(|error| error.to_string())?;
        }
        return Ok(());
    }
    window
        .set_position(Position::Physical(PhysicalPosition::new(
            bounds.x, bounds.y,
        )))
        .map_err(|error| error.to_string())?;
    window
        .set_size(PhysicalSize::new(
            bounds.width.max(1) as u32,
            bounds.height.max(1) as u32,
        ))
        .map_err(|error| error.to_string())
}

fn popup_motion_y(start_y: i32, target_y: i32, progress: f64, opening: bool) -> i32 {
    let eased = if opening {
        1.0 - (1.0 - progress).powi(5)
    } else {
        progress.powi(3)
    };
    (start_y as f64 + (target_y - start_y) as f64 * eased).round() as i32
}

fn animate_popup_position(
    window: &WebviewWindow,
    start: PhysicalWindowBounds,
    target: PhysicalWindowBounds,
    opening: bool,
    intent: u64,
) {
    let generation = POPUP_MOTION_GENERATION.fetch_add(1, Ordering::SeqCst) + 1;
    let app = window.app_handle().clone();
    let label = window.label().to_owned();
    std::thread::spawn(move || {
        let frames = if opening { 13 } else { 9 };
        let frame_delay = if opening { 15 } else { 13 };
        for frame in 1..=frames {
            if POPUP_MOTION_GENERATION.load(Ordering::SeqCst) != generation
                || POPUP_INTENT_GENERATION.load(Ordering::SeqCst) != intent
            {
                return;
            }
            let progress = frame as f64 / frames as f64;
            let bounds = PhysicalWindowBounds {
                x: target.x,
                y: popup_motion_y(start.y, target.y, progress, opening),
                width: target.width,
                height: target.height,
            };
            let app_for_frame = app.clone();
            let label_for_frame = label.clone();
            let (frame_done_tx, frame_done_rx) = mpsc::sync_channel(1);
            if app
                .run_on_main_thread(move || {
                    if POPUP_MOTION_GENERATION.load(Ordering::SeqCst) == generation
                        && POPUP_INTENT_GENERATION.load(Ordering::SeqCst) == intent
                    {
                        if let Some(active) = app_for_frame.get_webview_window(&label_for_frame) {
                            let _ = apply_window_bounds(&active, bounds);
                        }
                    }
                    let _ = frame_done_tx.send(());
                })
                .is_err()
            {
                return;
            }
            if frame_done_rx
                .recv_timeout(Duration::from_millis(80))
                .is_err()
            {
                return;
            }
            std::thread::sleep(Duration::from_millis(frame_delay));
        }
    });
}

fn anchored_resize_x(
    current_x: i32,
    current_width: i32,
    target_width: i32,
    monitor_left: i32,
    monitor_right: i32,
) -> i32 {
    let left_limit = monitor_left + 8;
    let right_limit = monitor_right - 8;
    let current_right = current_x + current_width;
    let left_gap = (current_x - left_limit).abs();
    let right_gap = (right_limit - current_right).abs();
    let anchored_x = if left_gap <= right_gap {
        current_x
    } else {
        current_right - target_width
    };
    anchored_x.clamp(left_limit, right_limit - target_width)
}

#[derive(Default)]
struct AppState {
    refresh_in_flight: Mutex<bool>,
}

#[tauri::command]
async fn fetch_usage(force: bool, state: State<'_, AppState>) -> Result<Vec<Value>, String> {
    {
        let mut in_flight = state
            .refresh_in_flight
            .lock()
            .map_err(|_| "refresh lock poisoned")?;
        if *in_flight {
            return Err("A refresh is already in progress.".to_string());
        }
        *in_flight = true;
    }

    let result = async {
        let suffix = if force {
            "/v1/usage?force=true"
        } else {
            "/v1/usage"
        };
        let response = reqwest::Client::new()
            .get(format!("{API_BASE}{suffix}"))
            // The desktop API starts alongside the popup. A connection attempted during that
            // small startup race must fail and let the frontend retry, not leave the popup in
            // "Refreshing..." forever with synthetic empty provider cards.
            .timeout(Duration::from_secs(3))
            .send()
            .await
            .map_err(|error| format!("The existing TokenBurn API is unavailable: {error}"))?;
        if !response.status().is_success() {
            return Err(format!("TokenBurn API returned HTTP {}", response.status()));
        }
        response
            .json::<Vec<Value>>()
            .await
            .map_err(|error| format!("TokenBurn API returned invalid JSON: {error}"))
    }
    .await;

    if let Ok(mut in_flight) = state.refresh_in_flight.lock() {
        *in_flight = false;
    }
    result
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct ShareClipboardPayload {
    // null when only the chart image is wanted (paste targets that drop images when text is
    // present, e.g. some chat composers).
    text: Option<String>,
    width: u32,
    height: u32,
    rgba_base64: String,
    png_base64: String,
}

// Chromium-family apps (ChatGPT, Edge, browser pastes) read image data from the registered "PNG"
// clipboard format, not CF_DIB. Register it once and write the PNG alongside the text and DIB so
// browser paste targets receive the chart instead of an empty upload box.
static PNG_CLIPBOARD_FORMAT: std::sync::OnceLock<u32> = std::sync::OnceLock::new();

fn png_clipboard_format() -> u32 {
    *PNG_CLIPBOARD_FORMAT.get_or_init(|| unsafe { RegisterClipboardFormatW(w!("PNG")) })
}

// The popup shares a chart image plus text in one atomic clipboard write: CF_UNICODETEXT for plain
// editors and assistant chats, CF_DIB (32bpp BI_RGB) for image targets. The image arrives as raw
// RGBA bytes from the WebView canvas so no image-decoding dependency is needed. Windows expects a
// bottom-up DIB, so the header marks the rows as already top-down with a negative height.
fn dib_bytes(width: u32, height: u32, rgba: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(40 + rgba.len());
    bytes.extend_from_slice(&40u32.to_le_bytes());
    bytes.extend_from_slice(&(width as i32).to_le_bytes());
    bytes.extend_from_slice(&(-(height as i32)).to_le_bytes());
    bytes.extend_from_slice(&1u16.to_le_bytes());
    bytes.extend_from_slice(&32u16.to_le_bytes());
    bytes.extend_from_slice(&0u32.to_le_bytes());
    bytes.extend_from_slice(&(rgba.len() as u32).to_le_bytes());
    bytes.extend_from_slice(&[0u8; 16]);
    for pixel in rgba.chunks_exact(4) {
        bytes.extend_from_slice(&[pixel[2], pixel[1], pixel[0], pixel[3]]);
    }
    bytes
}

unsafe fn alloc_global_bytes(bytes: &[u8]) -> Result<HGLOBAL, String> {
    // Exact size: clipboard consumers size binary formats via GlobalSize, and an extra
    // uninitialized byte would be read as part of the DIB or PNG payload.
    let handle =
        GlobalAlloc(GMEM_MOVEABLE, bytes.len()).map_err(|_| "GlobalAlloc failed".to_string())?;
    let locked = unsafe { GlobalLock(handle) };
    if locked.is_null() {
        let _ = GlobalFree(Some(handle));
        return Err("GlobalLock failed".to_string());
    }
    std::ptr::copy_nonoverlapping(bytes.as_ptr(), locked.cast::<u8>(), bytes.len());
    let _ = GlobalUnlock(handle);
    Ok(handle)
}

// Allocates every clipboard piece up front. A failure part-way must not leak the handles already
// allocated, so any successful allocations are freed before the error is returned.
fn alloc_global_pieces(pieces: &[(u32, Vec<u8>)]) -> Result<Vec<(u32, HGLOBAL)>, String> {
    let mut placements = Vec::with_capacity(pieces.len());
    for (format, bytes) in pieces {
        match unsafe { alloc_global_bytes(bytes) } {
            Ok(handle) => placements.push((*format, handle)),
            Err(error) => {
                for (_, handle) in &placements {
                    let _ = unsafe { GlobalFree(Some(*handle)) };
                }
                return Err(error);
            }
        }
    }
    Ok(placements)
}

#[tauri::command]
fn copy_share(payload: ShareClipboardPayload) -> Result<(), String> {
    use base64::Engine;
    if payload.width == 0 || payload.height == 0 || payload.width > 4096 || payload.height > 4096 {
        return Err("share image dimensions are invalid".into());
    }
    let expected = (payload.width as usize)
        .checked_mul(payload.height as usize)
        .and_then(|pixels| pixels.checked_mul(4))
        .ok_or("share image dimensions overflow")?;
    let rgba = base64::engine::general_purpose::STANDARD
        .decode(&payload.rgba_base64)
        .map_err(|_| "share image data is not valid base64")?;
    if rgba.len() != expected {
        return Err(format!(
            "share image size mismatch: expected {expected} bytes, got {}",
            rgba.len()
        ));
    }
    let png = base64::engine::general_purpose::STANDARD
        .decode(&payload.png_base64)
        .map_err(|_| "share image PNG is not valid base64")?;

    // (clipboard format, payload bytes). Handles the clipboard accepts are owned by the system
    // once SetClipboardData succeeds; rejected ones must be freed by us. Text is optional: an
    // image-only copy skips CF_UNICODETEXT so text-first paste targets attach the chart.
    // CF_UNICODETEXT consumers read until a UTF-16 null terminator, so the text carries one.
    let mut pieces = Vec::with_capacity(3);
    if let Some(text) = &payload.text {
        let text_bytes: Vec<u8> = text
            .encode_utf16()
            .chain(std::iter::once(0))
            .flat_map(|unit| unit.to_le_bytes())
            .collect();
        pieces.push((CF_UNICODETEXT.0 as u32, text_bytes));
    }
    let dib = dib_bytes(payload.width, payload.height, &rgba);
    pieces.push((CF_DIB.0 as u32, dib));
    let png_format = png_clipboard_format();
    if png_format != 0 {
        pieces.push((png_format, png));
    }
    let placements = alloc_global_pieces(&pieces)?;

    unsafe {
        if OpenClipboard(None).is_err() {
            for (_, handle) in &placements {
                let _ = GlobalFree(Some(*handle));
            }
            return Err("could not open the Windows clipboard".into());
        }
        if EmptyClipboard().is_err() {
            let _ = CloseClipboard();
            for (_, handle) in &placements {
                let _ = GlobalFree(Some(*handle));
            }
            return Err("could not empty the Windows clipboard".into());
        }
        let mut accepted = vec![false; placements.len()];
        for (index, (format, handle)) in placements.iter().enumerate() {
            accepted[index] = SetClipboardData(*format, Some(HANDLE(handle.0))).is_ok();
        }
        let _ = CloseClipboard();
        let all_accepted = accepted.iter().all(|ok| *ok);
        for (index, (_, handle)) in placements.iter().enumerate() {
            if !accepted[index] {
                let _ = GlobalFree(Some(*handle));
            }
        }
        if !all_accepted {
            return Err("the clipboard rejected part of the share payload".into());
        }
    }
    Ok(())
}

#[tauri::command]
async fn fetch_enabled_providers() -> Result<Vec<String>, String> {
    let response = reqwest::Client::new()
        .get("http://127.0.0.1:6738/providers")
        .timeout(Duration::from_secs(3))
        .send()
        .await
        .map_err(|error| format!("The desktop provider settings are unavailable: {error}"))?;
    if !response.status().is_success() {
        return Err(format!(
            "The desktop provider settings returned HTTP {}",
            response.status()
        ));
    }
    response
        .json::<Vec<String>>()
        .await
        .map_err(|error| format!("The desktop provider settings returned invalid JSON: {error}"))
}

#[tauri::command]
async fn request_desktop_refresh() -> Result<(), String> {
    let response = reqwest::Client::new()
        .post("http://127.0.0.1:6738/refresh")
        .timeout(Duration::from_secs(30))
        .send()
        .await
        .map_err(|error| format!("The desktop refresh service is unavailable: {error}"))?;
    if !response.status().is_success() {
        return Err(format!(
            "The desktop refresh service returned HTTP {}",
            response.status()
        ));
    }
    Ok(())
}

/// Resize the native popup before the web surface swaps layout. Keeping the current right edge
/// anchored matches the common taskbar/tray placement and avoids a popup jumping over the user.
#[tauri::command]
async fn set_breakdown_mode(
    window: WebviewWindow,
    expanded: bool,
    reduced_motion: bool,
) -> Result<f64, String> {
    POPUP_MOTION_REDUCED.store(reduced_motion, Ordering::SeqCst);
    let scale = window.scale_factor().unwrap_or(1.0);
    let monitor = window
        .current_monitor()
        .ok()
        .flatten()
        .or_else(|| window.primary_monitor().ok().flatten())
        .ok_or_else(|| "No display is available for the popup.".to_string())?;
    let monitor_size = monitor.size();
    let available = ((monitor_size.width as f64 / scale) - POPUP_SCREEN_MARGIN).max(1.0);
    let target_logical_width = if expanded {
        (BREAKDOWN_WIDTH as f64).min(available)
    } else {
        (POPUP_WIDTH as f64).min(available)
    };
    let current_size = window.outer_size().map_err(|error| error.to_string())?;
    let current_position = window.outer_position().map_err(|error| error.to_string())?;
    let requested_physical_width = (target_logical_width * scale).round() as i32;
    let monitor_position = monitor.position();
    let monitor_right = monitor_position.x + monitor_size.width as i32;
    let current_right = current_position.x + current_size.width as i32;
    let current_bounds = PhysicalWindowBounds {
        x: current_position.x,
        y: current_position.y,
        width: current_size.width as i32,
        height: current_size.height as i32,
    };
    let (target_x, target_y, target_physical_width, target_height) = if expanded {
        if let Ok(mut saved) = COMPACT_BREAKDOWN_BOUNDS.lock() {
            if saved.is_none() {
                *saved = Some(current_bounds);
            }
        }
        (
            anchored_resize_x(
                current_position.x,
                current_size.width as i32,
                requested_physical_width,
                monitor_position.x,
                monitor_right,
            ),
            current_position.y,
            requested_physical_width,
            current_size.height as i32,
        )
    } else {
        let saved = COMPACT_BREAKDOWN_BOUNDS
            .lock()
            .ok()
            .and_then(|mut value| value.take());
        let target = saved.unwrap_or(PhysicalWindowBounds {
            x: (current_right - requested_physical_width).clamp(
                monitor_position.x + 8,
                monitor_right - requested_physical_width - 8,
            ),
            y: current_position.y,
            width: requested_physical_width,
            height: current_size.height as i32,
        });
        (target.x, target.y, target.width, target.height)
    };
    let target_bounds = PhysicalWindowBounds {
        x: target_x,
        y: target_y,
        width: target_physical_width,
        height: target_height,
    };
    let generation = BREAKDOWN_GEOMETRY_GENERATION.fetch_add(1, Ordering::SeqCst) + 1;
    if reduced_motion {
        BREAKDOWN_GEOMETRY_ANIMATING.store(false, Ordering::SeqCst);
        apply_window_bounds(&window, target_bounds)?;
        return Ok(target_logical_width);
    }
    BREAKDOWN_GEOMETRY_ANIMATING.store(true, Ordering::SeqCst);
    let app = window.app_handle().clone();
    let label = window.label().to_owned();
    let start_bounds = current_bounds;
    let monitor_top = monitor_position.y;
    let monitor_bottom = monitor_position.y + monitor_size.height as i32;
    let anchor_bottom = (monitor_bottom - (start_bounds.y + start_bounds.height)).abs()
        <= (start_bounds.y - monitor_top).abs();
    let squash_px = 12.0 * scale;
    let (animation_done_tx, animation_done_rx) = mpsc::sync_channel(1);
    std::thread::spawn(move || {
        const FRAMES: i32 = 20;
        for frame in 1..=FRAMES {
            if BREAKDOWN_GEOMETRY_GENERATION.load(Ordering::SeqCst) != generation {
                break;
            }
            let progress = frame as f64 / FRAMES as f64;
            let eased = 1.0 - (1.0 - progress).powi(3);
            let bounds = animated_window_bounds(
                start_bounds,
                target_bounds,
                progress,
                eased,
                squash_px,
                anchor_bottom,
            );
            let app_for_frame = app.clone();
            let label_for_frame = label.clone();
            let (frame_done_tx, frame_done_rx) = mpsc::sync_channel(1);
            if app
                .run_on_main_thread(move || {
                    if BREAKDOWN_GEOMETRY_GENERATION.load(Ordering::SeqCst) != generation {
                        let _ = frame_done_tx.send(());
                        return;
                    }
                    let Some(active) = app_for_frame.get_webview_window(&label_for_frame) else {
                        let _ = frame_done_tx.send(());
                        return;
                    };
                    let _ = apply_window_bounds(&active, bounds);
                    let _ = frame_done_tx.send(());
                })
                .is_err()
            {
                break;
            }
            if frame_done_rx
                .recv_timeout(Duration::from_millis(80))
                .is_err()
            {
                break;
            }
            std::thread::sleep(Duration::from_millis(16));
        }
        if BREAKDOWN_GEOMETRY_GENERATION.load(Ordering::SeqCst) == generation {
            BREAKDOWN_GEOMETRY_ANIMATING.store(false, Ordering::SeqCst);
        }
        let _ = animation_done_tx.send(());
    });
    // Wait for frames that Windows has actually applied. The previous fire-and-forget loop queued
    // every resize and let DWM coalesce them, which made the window appear to snap to full width.
    let _ = animation_done_rx.recv_timeout(Duration::from_millis(650));
    Ok(target_logical_width)
}

#[tauri::command]
async fn fetch_refresh_status() -> Result<Value, String> {
    let response = reqwest::Client::new()
        .get("http://127.0.0.1:6738/refresh-status")
        .timeout(Duration::from_millis(900))
        .send()
        .await
        .map_err(|error| format!("The desktop refresh status is unavailable: {error}"))?;
    if !response.status().is_success() {
        return Err(format!(
            "The desktop refresh status returned HTTP {}",
            response.status()
        ));
    }
    response
        .json::<Value>()
        .await
        .map_err(|error| format!("The desktop refresh status returned invalid JSON: {error}"))
}

#[tauri::command]
fn open_claude_login() -> Result<(), String> {
    #[cfg(windows)]
    {
        use std::env;
        use std::os::windows::process::CommandExt;

        let mut candidates = Vec::new();
        if let Ok(app_data) = env::var("APPDATA") {
            candidates.push(std::path::PathBuf::from(app_data).join("npm\\claude.ps1"));
        }
        if let Ok(output) = Command::new("where.exe").arg("claude.ps1").output() {
            let stdout = String::from_utf8_lossy(&output.stdout);
            candidates.extend(stdout.lines().map(std::path::PathBuf::from));
        }
        let script = candidates
            .into_iter()
            .find(|path| path.is_file())
            .ok_or_else(|| {
                "Claude Code was not found on PATH. Install it, then try again.".to_string()
            })?;
        const CREATE_NEW_CONSOLE: u32 = 0x00000010;
        Command::new("powershell.exe")
            .creation_flags(CREATE_NEW_CONSOLE)
            .args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"])
            .arg(script)
            .arg("auth")
            .arg("login")
            .spawn()
            .map_err(|error| format!("Could not start Claude sign-in: {error}"))?;
        Ok(())
    }
    #[cfg(not(windows))]
    {
        Err("Claude sign-in is only available on Windows in this build.".to_string())
    }
}

#[tauri::command]
fn open_antigravity_login() -> Result<(), String> {
    #[cfg(windows)]
    {
        use std::env;
        use std::os::windows::process::CommandExt;

        // The Antigravity CLI ships through npm as an `agy.cmd` shim. Open it in its own console so
        // the user can complete the browser sign-in flow, then finish in the CLI.
        let mut candidates = Vec::new();
        if let Ok(app_data) = env::var("APPDATA") {
            candidates.push(std::path::PathBuf::from(app_data).join("npm\\agy.cmd"));
        }
        if let Ok(output) = Command::new("where.exe").arg("agy.cmd").output() {
            let stdout = String::from_utf8_lossy(&output.stdout);
            candidates.extend(stdout.lines().map(std::path::PathBuf::from));
        }
        let script = candidates
            .into_iter()
            .find(|path| path.is_file())
            .ok_or_else(|| {
                "The Antigravity CLI (agy) was not found on PATH. Install it, then try again."
                    .to_string()
            })?;
        const CREATE_NEW_CONSOLE: u32 = 0x00000010;
        Command::new("cmd.exe")
            .creation_flags(CREATE_NEW_CONSOLE)
            .arg("/c")
            .arg(script)
            .spawn()
            .map_err(|error| format!("Could not start Antigravity sign-in: {error}"))?;
        Ok(())
    }
    #[cfg(not(windows))]
    {
        Err("Antigravity sign-in is only available on Windows in this build.".to_string())
    }
}

#[tauri::command]
async fn get_settings_data() -> Result<Value, String> {
    let response = reqwest::Client::new()
        .get("http://127.0.0.1:6738/settings-data")
        .timeout(Duration::from_millis(900))
        .send()
        .await
        .map_err(|error| format!("The desktop settings surface is unavailable: {error}"))?;
    if !response.status().is_success() {
        return Err(format!(
            "The desktop settings surface returned HTTP {}",
            response.status()
        ));
    }
    response
        .json::<Value>()
        .await
        .map_err(|error| format!("The desktop settings surface returned invalid JSON: {error}"))
}

#[tauri::command]
async fn apply_settings_data(settings: Value) -> Result<(), String> {
    let response = reqwest::Client::new()
        .post("http://127.0.0.1:6738/settings-data")
        .timeout(Duration::from_millis(900))
        .json(&settings)
        .send()
        .await
        .map_err(|error| format!("The desktop settings surface is unavailable: {error}"))?;
    if !response.status().is_success() {
        return Err(format!(
            "The desktop settings surface returned HTTP {}",
            response.status()
        ));
    }
    Ok(())
}

#[tauri::command]
async fn set_spend_metric(metric: String) -> Result<(), String> {
    let response = reqwest::Client::new()
        .post("http://127.0.0.1:6738/spend-metric")
        .body(metric)
        .send()
        .await
        .map_err(|error| format!("The desktop settings surface is unavailable: {error}"))?;
    if !response.status().is_success() {
        return Err(format!(
            "The desktop settings surface returned HTTP {}",
            response.status()
        ));
    }
    Ok(())
}

#[tauri::command]
async fn get_diagnostics_bundle() -> Result<Value, String> {
    let response = reqwest::Client::new()
        .get("http://127.0.0.1:6738/diagnostics-bundle")
        .timeout(Duration::from_millis(2000))
        .send()
        .await
        .map_err(|error| format!("The desktop diagnostics surface is unavailable: {error}"))?;
    if !response.status().is_success() {
        return Err(format!(
            "The desktop diagnostics surface returned HTTP {}",
            response.status()
        ));
    }
    response
        .json::<Value>()
        .await
        .map_err(|error| format!("The desktop diagnostics surface returned invalid JSON: {error}"))
}

#[tauri::command]
fn set_screen_share_privacy(window: WebviewWindow, hidden: bool) -> Result<(), String> {
    #[cfg(windows)]
    {
        let affinity = if hidden {
            WDA_EXCLUDEFROMCAPTURE
        } else {
            WDA_NONE
        };
        let hwnd = window.hwnd().map_err(|error| error.to_string())?;
        unsafe {
            SetWindowDisplayAffinity(hwnd, affinity)
                .map_err(|error| format!("Could not update screen-share privacy: {error}"))?;
        }
    }
    #[cfg(not(windows))]
    {
        let _ = (window, hidden);
    }
    Ok(())
}

#[tauri::command]
fn hide_popup(window: WebviewWindow) -> Result<(), String> {
    let intent = POPUP_INTENT_GENERATION.fetch_add(1, Ordering::SeqCst) + 1;
    request_popup_close(&window, intent, false);
    Ok(())
}

#[tauri::command]
fn set_popup_motion_reduced(reduced: bool) {
    POPUP_MOTION_REDUCED.store(reduced, Ordering::SeqCst);
}

fn hide_popup_window(window: &WebviewWindow) -> Result<(), String> {
    window.hide().map_err(|error| error.to_string())?;
    native_visibility(window, false);
    notify_desktop_visibility("/popup-hidden");
    Ok(())
}

fn request_popup_close(window: &WebviewWindow, intent: u64, focus_dismiss: bool) {
    let _ = window.emit("poc-closing", intent);
    let current_size = window.outer_size().ok();
    let current_position = window.outer_position().ok();
    let scale = window.scale_factor().unwrap_or(1.0);
    let reduced = POPUP_MOTION_REDUCED.load(Ordering::SeqCst);
    let start_bounds =
        current_size
            .zip(current_position)
            .map(|(size, position)| PhysicalWindowBounds {
                x: position.x,
                y: position.y,
                width: size.width as i32,
                height: size.height as i32,
            });
    let target_bounds = start_bounds.map(|start| {
        let anchor_bottom = POPUP_ANCHOR_BOTTOM.load(Ordering::SeqCst);
        let motion_offset = (20.0 * scale).round() as i32;
        PhysicalWindowBounds {
            y: start.y
                + if anchor_bottom {
                    motion_offset
                } else {
                    -motion_offset
                },
            ..start
        }
    });
    if !reduced {
        if let (Some(start), Some(target)) = (start_bounds, target_bounds) {
            animate_popup_position(window, start, target, false, intent);
        }
    } else {
        POPUP_MOTION_GENERATION.fetch_add(1, Ordering::SeqCst);
    }
    let app = window.app_handle().clone();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(if reduced { 1 } else { 185 }));
        let popup_app = app.clone();
        let _ = app.run_on_main_thread(move || {
            if POPUP_INTENT_GENERATION.load(Ordering::SeqCst) != intent {
                return;
            }
            let Some(window) = popup_window(&popup_app) else {
                return;
            };
            if window.is_visible().unwrap_or(false) {
                let _ = hide_popup_window(&window);
                if let Some(start) = start_bounds {
                    let _ = apply_window_bounds(&window, start);
                }
                if focus_dismiss {
                    LAST_FOCUS_HIDE_MS.store(unix_now_ms(), Ordering::SeqCst);
                }
            }
        });
    });
}

fn notify_desktop_visibility(path: &'static str) {
    use std::io::Write;
    use std::net::TcpStream;

    // Keep popup visibility notifications ordered. Spawning one detached thread per event let a
    // later /popup-hidden arrive before an earlier /popup-shown, leaving the desktop host in the
    // wrong z-order state after a taskbar click.
    let Ok(mut stream) = TcpStream::connect_timeout(
        &"127.0.0.1:6738".parse().expect("valid loopback address"),
        Duration::from_millis(120),
    ) else {
        return;
    };
    let request = format!("GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
    let _ = stream.write_all(request.as_bytes());
}

fn native_visibility(window: &WebviewWindow, visible: bool) {
    if let Ok(hwnd) = window.hwnd() {
        set_native_visibility(hwnd, visible);
    }
}

fn set_native_visibility(hwnd: windows::Win32::Foundation::HWND, visible: bool) {
    unsafe {
        let root = GetAncestor(hwnd, GA_ROOT);
        let root = if root.0.is_null() { hwnd } else { root };
        let _ = ShowWindow(root, if visible { SW_SHOW } else { SW_HIDE });
    }
}

fn promote_popup(window: &WebviewWindow) {
    if let Ok(hwnd) = window.hwnd() {
        unsafe {
            let _ = SetWindowPos(
                hwnd,
                Some(HWND_TOPMOST),
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
            );
        }
    }
}

fn tray_image() -> Image<'static> {
    Image::from_bytes(include_bytes!(
        "../../../assets/brand/exports/tokenburn-mark-gray-32.png"
    ))
    .expect("TokenBurn tray icon resource is invalid")
    .to_owned()
}

fn popup_window(app: &AppHandle) -> Option<WebviewWindow> {
    app.get_webview_window("main")
}

fn query_coordinate(path: &str, key: &str) -> Option<f64> {
    path.split_once('?')?.1.split('&').find_map(|pair| {
        let (name, value) = pair.split_once('=')?;
        (name == key)
            .then(|| value.parse::<f64>().ok())
            .flatten()
            .filter(|coordinate| coordinate.is_finite() && coordinate.abs() < 100_000.0)
    })
}

fn query_string(path: &str, key: &str) -> Option<String> {
    path.split_once('?')?.1.split('&').find_map(|pair| {
        let (name, value) = pair.split_once('=')?;
        (name == key).then(|| value.to_string())
    })
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct LayoutRect {
    left: f64,
    top: f64,
    right: f64,
    bottom: f64,
}

impl LayoutRect {
    fn intersects(self, other: Self) -> bool {
        self.left < other.right
            && self.right > other.left
            && self.top < other.bottom
            && self.bottom > other.top
    }
}

fn query_avoid_rect(path: &str) -> Option<LayoutRect> {
    let left = query_coordinate(path, "avoidX")?;
    let top = query_coordinate(path, "avoidY")?;
    let width = query_coordinate(path, "avoidWidth")?;
    let height = query_coordinate(path, "avoidHeight")?;
    (width > 0.0 && height > 0.0).then_some(LayoutRect {
        left,
        top,
        right: left + width,
        bottom: top + height,
    })
}

fn clamp_popup(
    left: f64,
    top: f64,
    width: f64,
    height: f64,
    monitor: LayoutRect,
    gap: f64,
) -> (f64, f64) {
    let min_left = monitor.left + gap;
    let max_left = (monitor.right - width - gap).max(min_left);
    let min_top = monitor.top + gap;
    let max_top = (monitor.bottom - height - gap).max(min_top);
    (left.clamp(min_left, max_left), top.clamp(min_top, max_top))
}

/// Returns a popup size that fits the target display. The preferred size remains the familiar
/// 320 x 800 logical popover on roomy displays, but small displays must get a real viewport that
/// fits rather than a clipped native window.
fn popup_size_for_monitor(monitor: LayoutRect, scale: f64) -> (f64, f64) {
    let available_width = ((monitor.right - monitor.left) / scale - POPUP_SCREEN_MARGIN).max(1.0);
    let available_height = ((monitor.bottom - monitor.top) / scale - POPUP_SCREEN_MARGIN).max(1.0);
    (
        (POPUP_WIDTH as f64).min(available_width),
        (POPUP_HEIGHT as f64).min(available_height),
    )
}

fn calculate_popup_position(
    anchor_x: f64,
    anchor_y: f64,
    width: f64,
    height: f64,
    scale: f64,
    monitor: LayoutRect,
    avoid: Option<LayoutRect>,
) -> (f64, f64) {
    let gap = 8.0 * scale;
    let anchor_gap = 12.0 * scale;
    let mut left = anchor_x - width / 2.0;
    let mut top = anchor_y - height - anchor_gap;

    if let Some(avoid) = avoid {
        let horizontal = (avoid.right - avoid.left) >= (avoid.bottom - avoid.top);
        let at_bottom = horizontal && avoid.bottom >= monitor.bottom - 96.0 * scale;
        let at_top = horizontal && avoid.top <= monitor.top + 96.0 * scale;
        let at_left = !horizontal && avoid.left <= monitor.left + 96.0 * scale;

        if at_bottom {
            left = avoid.right - width;
            top = avoid.top - height - gap;
        } else if at_top {
            left = avoid.right - width;
            top = avoid.bottom + gap;
        } else if at_left {
            left = avoid.right + gap;
            top = avoid.bottom - height;
        } else if !horizontal {
            left = avoid.left - width - gap;
            top = avoid.bottom - height;
        }

        let (clamped_left, clamped_top) = clamp_popup(left, top, width, height, monitor, gap);
        let candidate = LayoutRect {
            left: clamped_left,
            top: clamped_top,
            right: clamped_left + width,
            bottom: clamped_top + height,
        };
        if !candidate.intersects(avoid) {
            return (clamped_left, clamped_top);
        }

        let alternatives = [
            (avoid.left - width - gap, avoid.bottom - height),
            (avoid.left, avoid.bottom + gap),
            (avoid.right - width, avoid.top - height - gap),
            (avoid.right + gap, avoid.top),
        ];
        for (alternative_left, alternative_top) in alternatives {
            let (candidate_left, candidate_top) = clamp_popup(
                alternative_left,
                alternative_top,
                width,
                height,
                monitor,
                gap,
            );
            let candidate = LayoutRect {
                left: candidate_left,
                top: candidate_top,
                right: candidate_left + width,
                bottom: candidate_top + height,
            };
            if !candidate.intersects(avoid) {
                return (candidate_left, candidate_top);
            }
        }
    }

    if top < monitor.top + gap {
        top = anchor_y + anchor_gap;
    }
    clamp_popup(left, top, width, height, monitor, gap)
}

fn unix_now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis() as u64)
        .unwrap_or_default()
}

fn write_control_response(mut stream: TcpStream, status: &str) {
    let response = format!("HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    let _ = stream.write_all(response.as_bytes());
}

fn handle_control_connection(mut stream: TcpStream, app: &AppHandle) {
    let mut buffer = [0u8; 4096];
    let Ok(read) = stream.read(&mut buffer) else {
        return;
    };
    let request = String::from_utf8_lossy(&buffer[..read]);
    let Some(path) = request
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
    else {
        write_control_response(stream, "400 Bad Request");
        return;
    };

    if path.starts_with("/show") {
        let x = query_coordinate(path, "x").unwrap_or(0.0);
        let y = query_coordinate(path, "y").unwrap_or(0.0);
        let avoid = query_avoid_rect(path);
        // Optional ?page=settings|customize: used by the tray's right-click menu so "Settings"
        // and "Customize" land on the same in-popup pages the Options button opens, instead of a
        // second native window racing this popup for focus and position.
        let page = query_string(path, "page");
        let app = app.clone();
        let _ = app.clone().run_on_main_thread(move || {
            if let Some(window) = popup_window(&app) {
                show_popup_at(&window, x, y, avoid);
                if let Some(page) = page {
                    // The popup may be shown for the first time by this request. Delay the
                    // navigation event until the webview has had a chance to attach its listener.
                    let page_window = window.clone();
                    std::thread::spawn(move || {
                        std::thread::sleep(Duration::from_millis(90));
                        let script = match page.as_str() {
                            "settings" => "window.dispatchEvent(new CustomEvent('usage-monitor-open-page', { detail: 'settings' }));",
                            "customize" => "window.dispatchEvent(new CustomEvent('usage-monitor-open-page', { detail: 'customize' }));",
                            _ => return,
                        };
                        let _ = page_window.eval(script);
                        let _ = page_window.emit("open-page", page);
                    });
                }
            }
        });
        write_control_response(stream, "204 No Content");
        return;
    }

    if path.starts_with("/hide") {
        let intent = POPUP_INTENT_GENERATION.fetch_add(1, Ordering::SeqCst) + 1;
        let app = app.clone();
        let _ = app.clone().run_on_main_thread(move || {
            if let Some(window) = popup_window(&app) {
                request_popup_close(&window, intent, false);
            }
        });
        write_control_response(stream, "204 No Content");
        return;
    }

    if path.starts_with("/toggle") {
        let x = query_coordinate(path, "x").unwrap_or(0.0);
        let y = query_coordinate(path, "y").unwrap_or(0.0);
        let avoid = query_avoid_rect(path);
        let app = app.clone();
        let _ = app.clone().run_on_main_thread(move || {
            toggle_popup(&app, x, y, avoid);
        });
        write_control_response(stream, "204 No Content");
        return;
    }

    write_control_response(stream, "404 Not Found");
}

fn start_control_server(app: AppHandle) {
    std::thread::spawn(move || {
        let Ok(listener) = TcpListener::bind(CONTROL_BIND) else {
            return;
        };
        for stream in listener.incoming().flatten() {
            handle_control_connection(stream, &app);
        }
    });
}

/// A caller with no real click coordinate (the tray menu's "Open dashboard") passes a non-finite
/// or origin anchor and gets the fallback. Kept separate from show_popup_at so it is testable
/// without a window handle.
fn resolve_anchor(x: f64, y: f64, fallback: (f64, f64)) -> (f64, f64) {
    let requested = x.is_finite() && y.is_finite() && !(x.abs() < 1.0 && y.abs() < 1.0);
    if requested {
        (x, y)
    } else {
        fallback
    }
}

fn show_popup_at(window: &WebviewWindow, x: f64, y: f64, avoid: Option<LayoutRect>) {
    BREAKDOWN_GEOMETRY_GENERATION.fetch_add(1, Ordering::SeqCst);
    BREAKDOWN_GEOMETRY_ANIMATING.store(false, Ordering::SeqCst);
    if let Ok(mut bounds) = COMPACT_BREAKDOWN_BOUNDS.lock() {
        *bounds = None;
    }
    let intent = POPUP_INTENT_GENERATION.fetch_add(1, Ordering::SeqCst) + 1;
    show_popup_at_once(window, x, y, avoid, intent, true);
    let _ = window.emit("poc-opened", ());
}

/// Re-evaluate once after Windows has finished a taskbar drag. Explorer can report the old
/// taskbar geometry for a moment after a cross-monitor move, which made the first popup open
/// land in a visibly wrong spot. The intent check prevents a late correction from reviving a
/// popup the user has already dismissed or moved again.
fn show_popup_at_once(
    window: &WebviewWindow,
    x: f64,
    y: f64,
    avoid: Option<LayoutRect>,
    intent: u64,
    schedule_settle: bool,
) {
    let fallback_anchor = window
        .primary_monitor()
        .ok()
        .flatten()
        .map(|monitor| {
            let position = monitor.position();
            let size = monitor.size();
            (
                position.x as f64 + size.width as f64 / 2.0,
                position.y as f64 + size.height as f64 / 2.0,
            )
        })
        .unwrap_or((960.0, 540.0));
    let (anchor_x, anchor_y) = resolve_anchor(x, y, fallback_anchor);
    let monitor = window
        .available_monitors()
        .ok()
        .and_then(|monitors| {
            monitors.into_iter().find_map(|monitor| {
                let position = monitor.position();
                let size = monitor.size();
                let left = position.x as f64;
                let top = position.y as f64;
                let right = left + size.width as f64;
                let bottom = top + size.height as f64;
                (anchor_x >= left && anchor_x < right && anchor_y >= top && anchor_y < bottom)
                    .then_some((left, top, right, bottom, monitor.scale_factor()))
            })
        })
        .or_else(|| {
            window.primary_monitor().ok().flatten().map(|monitor| {
                let position = monitor.position();
                let size = monitor.size();
                (
                    position.x as f64,
                    position.y as f64,
                    position.x as f64 + size.width as f64,
                    position.y as f64 + size.height as f64,
                    monitor.scale_factor(),
                )
            })
        })
        .unwrap_or((0.0, 0.0, 1920.0, 1080.0, 1.0));
    let (monitor_left, monitor_top, monitor_right, monitor_bottom, scale) = monitor;
    let monitor_rect = LayoutRect {
        left: monitor_left,
        top: monitor_top,
        right: monitor_right,
        bottom: monitor_bottom,
    };
    // Tauri converts logical sizes to the active monitor's DPI itself. Supplying a PhysicalSize
    // here made the WebView use the scaled number as its CSS viewport after a monitor change,
    // turning a 320px popover into an oversized dashboard on high-DPI displays.
    let (logical_width, logical_height) = popup_size_for_monitor(monitor_rect, scale);
    let width = logical_width * scale;
    let height = logical_height * scale;
    let (left, top) = calculate_popup_position(
        anchor_x,
        anchor_y,
        width,
        height,
        scale,
        monitor_rect,
        avoid,
    );
    let target_bounds = PhysicalWindowBounds {
        x: left as i32,
        y: top as i32,
        width: width.round() as i32,
        height: height.round() as i32,
    };
    if !schedule_settle {
        let _ = window.set_size(LogicalSize::new(logical_width, logical_height));
        let _ = window.set_position(Position::Physical(PhysicalPosition::new(
            left as i32,
            top as i32,
        )));
        return;
    }
    // Apply geometry while hidden. Showing first lets WebView2 paint a frame at its old
    // (often 0,0) bounds, and the focus-lost handler can hide it again before the move lands.
    REVEALING.store(true, Ordering::SeqCst);
    POPUP_MOTION_GENERATION.fetch_add(1, Ordering::SeqCst);
    let _ = window.hide();
    native_visibility(window, false);
    let _ = window.set_size(LogicalSize::new(logical_width, logical_height));
    let reduced = POPUP_MOTION_REDUCED.load(Ordering::SeqCst);
    // The popup is deliberately tall, so its nearest monitor edge is not a reliable proxy for
    // the taskbar side. Use the actual click/tray anchor. A bottom taskbar must make the window
    // rise from below and dismiss back down even when the popup itself sits nearer the top edge.
    let anchor_bottom = anchor_y >= monitor_top + (monitor_bottom - monitor_top) / 2.0;
    POPUP_ANCHOR_BOTTOM.store(anchor_bottom, Ordering::SeqCst);
    let motion_offset = 26.0 * scale;
    let start_top = if reduced {
        top
    } else if anchor_bottom {
        top + motion_offset
    } else {
        top - motion_offset
    };
    let _ = window.set_position(Position::Physical(PhysicalPosition::new(
        left as i32,
        start_top as i32,
    )));
    // The anchor monitor is authoritative. Querying current_monitor immediately after moving a
    // hidden WebView can return its old monitor and was the source of the lower-left fragment.
    let _ = window.show();
    native_visibility(window, true);
    promote_popup(window);
    notify_desktop_visibility("/popup-shown");
    let _ = window.set_focus();
    if !reduced {
        animate_popup_position(
            window,
            PhysicalWindowBounds {
                y: start_top as i32,
                ..target_bounds
            },
            target_bounds,
            true,
            intent,
        );
    }
    if schedule_settle {
        let app = window.app_handle().clone();
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(if reduced { 40 } else { 260 }));
            let popup_app = app.clone();
            let _ = app.run_on_main_thread(move || {
                if POPUP_INTENT_GENERATION.load(Ordering::SeqCst) != intent {
                    return;
                }
                let Some(window) = popup_window(&popup_app) else {
                    return;
                };
                if window.is_visible().unwrap_or(false) {
                    show_popup_at_once(&window, x, y, avoid, intent, false);
                }
            });
        });
    }
    std::thread::spawn(|| {
        std::thread::sleep(Duration::from_millis(300));
        REVEALING.store(false, Ordering::SeqCst);
    });
}

fn toggle_popup(app: &AppHandle, x: f64, y: f64, avoid: Option<LayoutRect>) {
    let Some(window) = popup_window(app) else {
        return;
    };
    let intent = POPUP_INTENT_GENERATION.fetch_add(1, Ordering::SeqCst) + 1;
    if window.is_visible().unwrap_or(false) {
        request_popup_close(&window, intent, false);
        LAST_FOCUS_HIDE_MS.store(0, Ordering::SeqCst);
    } else {
        let last_focus_hide = LAST_FOCUS_HIDE_MS.load(Ordering::SeqCst);
        if last_focus_hide > 0 && unix_now_ms().saturating_sub(last_focus_hide) < 450 {
            LAST_FOCUS_HIDE_MS.store(0, Ordering::SeqCst);
            return;
        }
        if POPUP_INTENT_GENERATION.load(Ordering::SeqCst) != intent {
            return;
        }
        show_popup_at(&window, x, y, avoid);
    }
}

fn second_instance_activation_target(_args: &[String]) -> &'static str {
    "main"
}

fn reveal_existing_instance(app: &AppHandle, args: &[String]) {
    // A second direct launch must not leave a hidden popup looking like a dead application.
    // The single-instance plugin exits the new process after this callback returns.
    match second_instance_activation_target(args) {
        "main" => {
            if let Some(window) = popup_window(app) {
                show_popup_at(&window, f64::NAN, f64::NAN, None);
            }
        }
        _ => unreachable!("all TokenBurn second launches target the popup"),
    }
}

fn set_tokenburn_app_user_model_id() {
    // Match the WPF shell identity so Windows uses one product name and icon family in shell UI.
    let app_id = HSTRING::from("TokenBurn");
    let _ = unsafe { SetCurrentProcessExplicitAppUserModelID(&app_id) };
}

fn main() {
    set_tokenburn_app_user_model_id();
    let hosted = std::env::args().any(|arg| arg.eq_ignore_ascii_case("--hosted"));
    tauri::Builder::default()
        // This must remain the first plugin. It prevents a second popup host from creating its
        // own WebView2 process tree before the primary instance can receive the launch request.
        .plugin(tauri_plugin_single_instance::init(|app, args, _cwd| {
            reveal_existing_instance(app, &args);
        }))
        .manage(AppState::default())
        .invoke_handler(tauri::generate_handler![
            fetch_usage,
            fetch_enabled_providers,
            request_desktop_refresh,
            set_breakdown_mode,
            set_popup_motion_reduced,
            fetch_refresh_status,
            hide_popup,
            open_claude_login,
            open_antigravity_login,
            get_settings_data,
            apply_settings_data,
            get_diagnostics_bundle,
            set_spend_metric,
            set_screen_share_privacy,
            copy_share
        ])
        .setup(move |app| {
            start_control_server(app.handle().clone());
            let app_handle = app.handle().clone();
            if !hosted {
                let open = MenuItem::with_id(app, "open", "Open dashboard", true, None::<&str>)?;
                let refresh = MenuItem::with_id(app, "refresh", "Refresh", true, None::<&str>)?;
                let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
                let tray_menu = Menu::new(app)?;
                tray_menu.append(&open)?;
                tray_menu.append(&refresh)?;
                tray_menu.append(&quit)?;
                let tray = TrayIconBuilder::with_id("tokenburn")
                    .menu(&tray_menu)
                    .show_menu_on_left_click(false)
                    .tooltip("TokenBurn")
                    .icon(tray_image())
                    .on_tray_icon_event(move |_tray, event| {
                        if let TrayIconEvent::Click {
                            button: MouseButton::Left,
                            button_state: MouseButtonState::Up,
                            position,
                            ..
                        } = event
                        {
                            toggle_popup(&app_handle, position.x, position.y, None);
                        }
                    })
                    .build(app)?;
                tray.set_tooltip(Some("TokenBurn"))?;
            }
            let initial_app = app.handle().clone();
            let reveal_for_capture = std::env::var_os("USAGE_MONITOR_POC_REVEAL").is_some();
            std::thread::spawn(move || {
                std::thread::sleep(Duration::from_millis(if reveal_for_capture {
                    2200
                } else if hosted {
                    200
                } else {
                    1200
                }));
                if let Some(window) = initial_app.get_webview_window("main") {
                    if reveal_for_capture {
                        show_popup_at(&window, 1670.0, 1080.0, None);
                    } else {
                        let _ = window.hide();
                        native_visibility(&window, false);
                    }
                }
            });
            Ok(())
        })
        .on_menu_event(|app, event| match event.id().as_ref() {
            "open" => {
                if let Some(window) = popup_window(app) {
                    // No click coordinate to anchor to here. A non-finite anchor makes
                    // show_popup_at fall back to the centre of the primary monitor; the literal
                    // (1000, 1000) this used to pass is only sensible on a ~1080p primary display
                    // and lands somewhere unrelated on anything else.
                    show_popup_at(&window, f64::NAN, f64::NAN, None);
                }
            }
            "refresh" => {
                if let Some(window) = popup_window(app) {
                    let _ = window.emit("poc-refresh", true);
                }
            }
            "quit" => app.exit(0),
            _ => {}
        })
        .on_window_event(|window, event| {
            let WindowEvent::Focused(focused) = event else {
                return;
            };
            let focus_generation = FOCUS_GENERATION.fetch_add(1, Ordering::SeqCst) + 1;
            if *focused || REVEALING.load(Ordering::SeqCst) {
                return;
            }

            // Focus loss is intentionally delayed. The taskbar strip receives the second click
            // through the desktop host, and that click must cancel this hide rather than race it
            // into a hide-then-reopen flicker.
            let intent_generation = POPUP_INTENT_GENERATION.load(Ordering::SeqCst);
            let hide_delay = if BREAKDOWN_GEOMETRY_ANIMATING.load(Ordering::SeqCst) {
                380
            } else {
                220
            };
            let window = window.clone();
            let app = window.app_handle().clone();
            std::thread::spawn(move || {
                std::thread::sleep(Duration::from_millis(hide_delay));
                let intent_unchanged =
                    POPUP_INTENT_GENERATION.load(Ordering::SeqCst) == intent_generation;
                let focus_unchanged = FOCUS_GENERATION.load(Ordering::SeqCst) == focus_generation;
                if !REVEALING.load(Ordering::SeqCst)
                    && !BREAKDOWN_GEOMETRY_ANIMATING.load(Ordering::SeqCst)
                    && intent_unchanged
                    && focus_unchanged
                    && !window.is_focused().unwrap_or(false)
                    && window.is_visible().unwrap_or(false)
                {
                    let popup_app = app.clone();
                    let _ = app.run_on_main_thread(move || {
                        if window.is_visible().unwrap_or(false) {
                            if let Some(webview) = popup_window(&popup_app) {
                                request_popup_close(&webview, intent_generation, true);
                            }
                        }
                    });
                }
            });
        })
        .run(tauri::generate_context!())
        .expect("error while running TokenBurn");
}

#[cfg(test)]
mod tests {
    use super::{
        alloc_global_bytes, anchored_resize_x, animated_window_bounds, calculate_popup_position,
        dib_bytes, png_clipboard_format, popup_motion_y, popup_size_for_monitor, resolve_anchor,
        second_instance_activation_target, LayoutRect, PhysicalWindowBounds,
    };
    use base64::Engine;
    use windows::Win32::Foundation::{HANDLE, HGLOBAL};
    use windows::Win32::System::DataExchange::{
        CloseClipboard, EmptyClipboard, GetClipboardData, OpenClipboard, SetClipboardData,
    };
    use windows::Win32::System::Memory::{GlobalLock, GlobalSize, GlobalUnlock};
    use windows::Win32::System::Ole::{CF_DIB, CF_UNICODETEXT};

    const MONITOR: LayoutRect = LayoutRect {
        left: 0.0,
        top: 0.0,
        right: 1920.0,
        bottom: 1080.0,
    };

    fn strip(left: f64, top: f64, right: f64, bottom: f64) -> LayoutRect {
        LayoutRect {
            left,
            top,
            right,
            bottom,
        }
    }

    fn popup_at(position: (f64, f64)) -> LayoutRect {
        LayoutRect {
            left: position.0,
            top: position.1,
            right: position.0 + 320.0,
            bottom: position.1 + 800.0,
        }
    }

    #[test]
    fn popup_stays_above_bottom_strip() {
        let position = calculate_popup_position(
            200.0,
            1060.0,
            320.0,
            800.0,
            1.0,
            MONITOR,
            Some(strip(0.0, 1040.0, 400.0, 1080.0)),
        );

        assert_eq!(position, (80.0, 232.0));
        assert!(!popup_at(position).intersects(strip(0.0, 1040.0, 400.0, 1080.0)));
    }

    #[test]
    fn popup_stays_below_top_strip() {
        let position = calculate_popup_position(
            200.0,
            20.0,
            320.0,
            800.0,
            1.0,
            MONITOR,
            Some(strip(0.0, 0.0, 400.0, 40.0)),
        );

        assert_eq!(position, (80.0, 48.0));
        assert!(!popup_at(position).intersects(strip(0.0, 0.0, 400.0, 40.0)));
    }

    #[test]
    fn popup_sits_beside_vertical_strip() {
        let left = strip(0.0, 0.0, 40.0, 1080.0);
        let right = strip(1880.0, 0.0, 1920.0, 1080.0);

        let left_position =
            calculate_popup_position(20.0, 500.0, 320.0, 800.0, 1.0, MONITOR, Some(left));
        let right_position =
            calculate_popup_position(1900.0, 500.0, 320.0, 800.0, 1.0, MONITOR, Some(right));

        assert_eq!(left_position, (48.0, 272.0));
        assert_eq!(right_position, (1552.0, 272.0));
        assert!(!popup_at(left_position).intersects(left));
        assert!(!popup_at(right_position).intersects(right));
    }

    #[test]
    fn anchorless_open_falls_back_to_the_monitor_centre() {
        // The tray menu's "Open dashboard" has no click coordinate. It used to pass a literal
        // (1000, 1000), which is only sensible on a ~1080p primary display.
        let centre = (1280.0, 720.0);
        assert_eq!(resolve_anchor(f64::NAN, f64::NAN, centre), centre);
        assert_eq!(resolve_anchor(0.0, 0.0, centre), centre);
        assert_eq!(resolve_anchor(f64::INFINITY, 500.0, centre), centre);
    }

    #[test]
    fn a_real_click_anchor_is_used_as_given() {
        let centre = (1280.0, 720.0);
        assert_eq!(resolve_anchor(1900.0, 1050.0, centre), (1900.0, 1050.0));
        // Negative coordinates are valid on a monitor left of the primary.
        assert_eq!(resolve_anchor(-2400.0, 300.0, centre), (-2400.0, 300.0));
    }

    #[test]
    fn popup_size_shrinks_to_fit_a_small_display() {
        let small_monitor = LayoutRect {
            left: 0.0,
            top: 0.0,
            right: 280.0,
            bottom: 620.0,
        };
        assert_eq!(popup_size_for_monitor(small_monitor, 1.0), (264.0, 604.0));
    }

    #[test]
    fn popup_size_preserves_the_preferred_size_on_large_displays() {
        assert_eq!(popup_size_for_monitor(MONITOR, 1.0), (320.0, 800.0));
    }

    #[test]
    fn popup_size_stays_logical_on_high_dpi_displays() {
        let high_dpi_monitor = LayoutRect {
            left: 0.0,
            top: 0.0,
            right: 3840.0,
            bottom: 2160.0,
        };
        assert_eq!(
            popup_size_for_monitor(high_dpi_monitor, 2.0),
            (320.0, 800.0)
        );
    }

    #[test]
    fn popup_is_clamped_inside_a_high_dpi_monitor() {
        let monitor = LayoutRect {
            left: -2560.0,
            top: 0.0,
            right: 0.0,
            bottom: 1440.0,
        };
        let position = calculate_popup_position(-2500.0, 1400.0, 640.0, 1200.0, 2.0, monitor, None);

        assert!(position.0 >= monitor.left + 16.0);
        assert!(position.1 >= monitor.top + 16.0);
        assert!(position.0 + 640.0 <= monitor.right - 16.0);
        assert!(position.1 + 1200.0 <= monitor.bottom - 16.0);
    }

    #[test]
    fn breakdown_expands_from_the_nearest_left_edge() {
        assert_eq!(anchored_resize_x(8, 320, 920, 0, 1920), 8);
    }

    #[test]
    fn breakdown_expands_from_the_nearest_right_edge() {
        assert_eq!(anchored_resize_x(1592, 320, 920, 0, 1920), 992);
    }

    #[test]
    fn breakdown_motion_squashes_while_preserving_the_bottom_edge() {
        let start = PhysicalWindowBounds {
            x: 1592,
            y: 232,
            width: 320,
            height: 800,
        };
        let target = PhysicalWindowBounds {
            x: 992,
            y: 232,
            width: 920,
            height: 800,
        };
        let midpoint = animated_window_bounds(start, target, 0.5, 0.5, 9.0, true);

        assert_eq!(midpoint.height, 791);
        assert_eq!(midpoint.y + midpoint.height, start.y + start.height);
        assert_eq!(
            animated_window_bounds(start, target, 1.0, 1.0, 9.0, true).height,
            800
        );
    }

    #[test]
    fn popup_motion_reaches_both_endpoints() {
        assert_eq!(popup_motion_y(100, 74, 0.0, true), 100);
        assert_eq!(popup_motion_y(100, 74, 1.0, true), 74);
        assert_eq!(popup_motion_y(74, 94, 0.0, false), 74);
        assert_eq!(popup_motion_y(74, 94, 1.0, false), 94);
    }

    #[test]
    fn popup_open_arrives_quickly_and_close_accelerates_away() {
        let open_midpoint = popup_motion_y(100, 74, 0.5, true);
        let close_midpoint = popup_motion_y(74, 94, 0.5, false);

        assert!(
            open_midpoint < 80,
            "open should settle near its destination by halfway"
        );
        assert!(
            close_midpoint < 80,
            "close should stay near its origin until halfway"
        );
    }

    #[test]
    fn direct_second_launch_is_forwarded_to_the_existing_popup() {
        assert_eq!(second_instance_activation_target(&[]), "main");
        assert_eq!(
            second_instance_activation_target(&["--hosted".to_string()]),
            "main"
        );
    }

    // Writes a real text + bitmap payload to the system clipboard and reads it back through the
    // same formats a paste target would use. This replaces the dev machine's clipboard content.
    #[test]
    fn share_clipboard_round_trip() {
        const WIDTH: u32 = 2;
        const HEIGHT: u32 = 2;
        let rgba = [
            255u8, 0, 0, 255, 0, 0, 255, 255, //
            0, 255, 0, 255, 255, 255, 0, 255,
        ];
        let dib = dib_bytes(WIDTH, HEIGHT, &rgba);
        let text_bytes: Vec<u8> = "TokenBurn · test"
            .encode_utf16()
            .chain(std::iter::once(0))
            .flat_map(|unit| unit.to_le_bytes())
            .collect();
        // A real 1x1 PNG so the read-back verifies the exact bytes a Chromium paste would decode.
        let png: Vec<u8> = base64::engine::general_purpose::STANDARD
            .decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")
            .unwrap();
        let png_format = png_clipboard_format();
        assert_ne!(png_format, 0, "the PNG clipboard format must register");

        let text_handle = unsafe { alloc_global_bytes(&text_bytes) }.unwrap();
        let dib_handle = unsafe { alloc_global_bytes(&dib) }.unwrap();
        let png_handle = unsafe { alloc_global_bytes(&png) }.unwrap();
        unsafe {
            assert!(OpenClipboard(None).is_ok());
            assert!(EmptyClipboard().is_ok());
            assert!(SetClipboardData(CF_UNICODETEXT.0 as u32, Some(HANDLE(text_handle.0))).is_ok());
            assert!(SetClipboardData(CF_DIB.0 as u32, Some(HANDLE(dib_handle.0))).is_ok());
            assert!(SetClipboardData(png_format, Some(HANDLE(png_handle.0))).is_ok());
            let _ = CloseClipboard();
        }

        unsafe {
            assert!(OpenClipboard(None).is_ok());
            let handle =
                GetClipboardData(CF_UNICODETEXT.0 as u32).expect("text format must be present");
            assert_eq!(
                GlobalSize(HGLOBAL(handle.0)),
                text_bytes.len(),
                "the text allocation must be exactly the payload size, terminator included"
            );
            let locked = GlobalLock(HGLOBAL(handle.0));
            assert!(!locked.is_null(), "text memory must lock");
            let copied = std::slice::from_raw_parts(locked.cast::<u8>(), text_bytes.len()).to_vec();
            let _ = GlobalUnlock(HGLOBAL(handle.0));
            let units = copied
                .chunks_exact(2)
                .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
                .collect::<Vec<u16>>();
            let units = units.strip_suffix(&[0u16]).unwrap_or(&units);
            assert_eq!(
                String::from_utf16(units).unwrap(),
                "TokenBurn · test",
                "pasted text must survive the round trip (with a null terminator)"
            );

            let handle = GetClipboardData(CF_DIB.0 as u32).expect("image format must be present");
            assert_eq!(
                GlobalSize(HGLOBAL(handle.0)),
                dib.len(),
                "the DIB allocation must be exactly the payload size"
            );
            let locked = GlobalLock(HGLOBAL(handle.0));
            assert!(!locked.is_null(), "image memory must lock");
            let copied = std::slice::from_raw_parts(locked.cast::<u8>(), dib.len()).to_vec();
            let _ = GlobalUnlock(HGLOBAL(handle.0));
            assert_eq!(copied, dib, "the clipboard DIB must survive the round trip");

            let handle = GetClipboardData(png_format).expect("PNG format must be present");
            assert_eq!(
                GlobalSize(HGLOBAL(handle.0)),
                png.len(),
                "the PNG allocation must be exactly the payload size"
            );
            let locked = GlobalLock(HGLOBAL(handle.0));
            assert!(!locked.is_null(), "PNG memory must lock");
            let copied = std::slice::from_raw_parts(locked.cast::<u8>(), png.len()).to_vec();
            let _ = GlobalUnlock(HGLOBAL(handle.0));
            assert_eq!(copied, png, "the clipboard PNG must survive the round trip");
            let _ = CloseClipboard();
        }

        // Image-only copy phase: no text placement means no CF_UNICODETEXT on the clipboard,
        // which is what makes text-first chat composers attach the chart instead of pasting only
        // the text. Kept in the same test as the full round trip because the system clipboard is
        // process-global and these tests would otherwise race each other's clipboard writes.
        let image_only_dib = dib_bytes(2, 2, &rgba);
        let dib_handle = unsafe { alloc_global_bytes(&image_only_dib) }.unwrap();
        let png_handle = unsafe { alloc_global_bytes(&png) }.unwrap();
        unsafe {
            assert!(OpenClipboard(None).is_ok());
            assert!(EmptyClipboard().is_ok());
            assert!(SetClipboardData(CF_DIB.0 as u32, Some(HANDLE(dib_handle.0))).is_ok());
            assert!(SetClipboardData(png_format, Some(HANDLE(png_handle.0))).is_ok());
            let _ = CloseClipboard();
        }
        unsafe {
            assert!(OpenClipboard(None).is_ok());
            assert!(
                GetClipboardData(CF_UNICODETEXT.0 as u32).is_err(),
                "an image-only copy must not leave a text format behind"
            );
            assert!(GetClipboardData(CF_DIB.0 as u32).is_ok());
            assert!(GetClipboardData(png_format).is_ok());
            let _ = CloseClipboard();
        }
    }
}
