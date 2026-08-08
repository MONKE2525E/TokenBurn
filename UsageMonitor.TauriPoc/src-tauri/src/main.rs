#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde_json::Value;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::process::Command;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tauri::image::Image;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{
    AppHandle, Emitter, Manager, PhysicalPosition, PhysicalSize, Position, State, WebviewWindow,
    Window, WindowEvent,
};
use windows::Win32::UI::WindowsAndMessaging::{
    GetAncestor, SetWindowDisplayAffinity, SetWindowPos, ShowWindow, GA_ROOT, HWND_TOPMOST,
    SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SW_HIDE, SW_SHOW, WDA_EXCLUDEFROMCAPTURE, WDA_NONE,
};

const API_BASE: &str = "http://127.0.0.1:6736";
const CONTROL_BIND: &str = "127.0.0.1:6737";
// Match DashboardView.swift and PanelHeightController.swift from OpenUsage. These are logical
// pixels, then Tauri applies the selected monitor's DPI scale at the native window boundary.
const POPUP_WIDTH: i32 = 320;
const POPUP_HEIGHT: i32 = 800;
static REVEALING: AtomicBool = AtomicBool::new(false);
static FOCUS_GENERATION: AtomicU64 = AtomicU64::new(0);
static POPUP_INTENT_GENERATION: AtomicU64 = AtomicU64::new(0);
static LAST_FOCUS_HIDE_MS: AtomicU64 = AtomicU64::new(0);

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
            .send()
            .await
            .map_err(|error| format!("The existing Usage Monitor API is unavailable: {error}"))?;
        if !response.status().is_success() {
            return Err(format!(
                "Usage Monitor API returned HTTP {}",
                response.status()
            ));
        }
        response
            .json::<Vec<Value>>()
            .await
            .map_err(|error| format!("Usage Monitor API returned invalid JSON: {error}"))
    }
    .await;

    if let Ok(mut in_flight) = state.refresh_in_flight.lock() {
        *in_flight = false;
    }
    result
}

#[tauri::command]
async fn fetch_enabled_providers() -> Result<Vec<String>, String> {
    let response = reqwest::Client::new()
        .get("http://127.0.0.1:6738/providers")
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
    POPUP_INTENT_GENERATION.fetch_add(1, Ordering::SeqCst);
    hide_popup_window(&window)?;
    Ok(())
}

fn hide_popup_window(window: &WebviewWindow) -> Result<(), String> {
    window.hide().map_err(|error| error.to_string())?;
    native_visibility(window, false);
    notify_desktop_visibility("/popup-hidden");
    Ok(())
}

fn hide_popup_native_window(window: &Window) -> Result<(), String> {
    window.hide().map_err(|error| error.to_string())?;
    native_window_visibility(window, false);
    notify_desktop_visibility("/popup-hidden");
    Ok(())
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

fn native_window_visibility(window: &Window, visible: bool) {
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
    const SIZE: usize = 32;
    let mut pixels = vec![0u8; SIZE * SIZE * 4];
    let mut set = |x: usize, y: usize, color: [u8; 4]| {
        if x >= SIZE || y >= SIZE {
            return;
        }
        let index = (y * SIZE + x) * 4;
        pixels[index..index + 4].copy_from_slice(&color);
    };
    let background = [18, 26, 34, 245];
    let accent = [56, 206, 190, 255];
    let highlight = [242, 246, 248, 255];
    for y in 3usize..29 {
        for x in 3usize..29 {
            let dx = if x < 7 { 7 - x } else { x.saturating_sub(24) };
            let dy = if y < 7 { 7 - y } else { y.saturating_sub(24) };
            if dx * dx + dy * dy <= 16 {
                set(x, y, background);
            }
        }
    }
    for y in 20..25 {
        for x in 8..12 {
            set(x, y, accent);
        }
    }
    for y in 15..25 {
        for x in 14..18 {
            set(x, y, accent);
        }
    }
    for y in 10..25 {
        for x in 20..24 {
            set(x, y, highlight);
        }
    }
    for x in 8..25 {
        set(x, 26, [106, 127, 139, 255]);
    }
    Image::new_owned(pixels, SIZE as u32, SIZE as u32)
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
                let _ = window.emit("poc-opened", ());
                if let Some(page) = page {
                    let _ = window.emit("open-page", page);
                }
            }
        });
        write_control_response(stream, "204 No Content");
        return;
    }

    if path.starts_with("/hide") {
        POPUP_INTENT_GENERATION.fetch_add(1, Ordering::SeqCst);
        let app = app.clone();
        let _ = app.clone().run_on_main_thread(move || {
            if let Some(window) = popup_window(&app) {
                let _ = hide_popup_window(&window);
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

fn show_popup_at(window: &WebviewWindow, x: f64, y: f64, avoid: Option<LayoutRect>) {
    let requested_anchor = x.is_finite() && y.is_finite() && !(x.abs() < 1.0 && y.abs() < 1.0);
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
    let (anchor_x, anchor_y) = if requested_anchor {
        (x, y)
    } else {
        fallback_anchor
    };
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
    let width = POPUP_WIDTH as f64 * scale;
    let height = POPUP_HEIGHT as f64 * scale;
    let monitor_rect = LayoutRect {
        left: monitor_left,
        top: monitor_top,
        right: monitor_right,
        bottom: monitor_bottom,
    };
    let (left, top) = calculate_popup_position(
        anchor_x,
        anchor_y,
        width,
        height,
        scale,
        monitor_rect,
        avoid,
    );
    // Apply geometry while hidden. Showing first lets WebView2 paint a frame at its old
    // (often 0,0) bounds, and the focus-lost handler can hide it again before the move lands.
    REVEALING.store(true, Ordering::SeqCst);
    let _ = window.hide();
    native_visibility(window, false);
    let _ = window.set_size(PhysicalSize::new(width as u32, height as u32));
    let _ = window.set_position(Position::Physical(PhysicalPosition::new(
        left as i32,
        top as i32,
    )));
    // The anchor monitor is authoritative. Querying current_monitor immediately after moving a
    // hidden WebView can return its old monitor and was the source of the lower-left fragment.
    let _ = window.show();
    native_visibility(window, true);
    promote_popup(window);
    notify_desktop_visibility("/popup-shown");
    let _ = window.set_focus();
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
        let _ = hide_popup_window(&window);
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
        let _ = window.emit("poc-opened", ());
    }
}

fn main() {
    let hosted = std::env::args().any(|arg| arg.eq_ignore_ascii_case("--hosted"));
    tauri::Builder::default()
        .manage(AppState::default())
        .invoke_handler(tauri::generate_handler![
            fetch_usage,
            fetch_enabled_providers,
            request_desktop_refresh,
            fetch_refresh_status,
            hide_popup,
            open_claude_login,
            get_settings_data,
            apply_settings_data,
            set_spend_metric,
            set_screen_share_privacy
        ])
        .setup(move |app| {
            start_control_server(app.handle().clone());
            let app_handle = app.handle().clone();
            if !hosted {
                let open =
                    MenuItem::with_id(app, "open", "Open Usage Monitor", true, None::<&str>)?;
                let refresh = MenuItem::with_id(app, "refresh", "Refresh", true, None::<&str>)?;
                let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
                let tray_menu = Menu::new(app)?;
                tray_menu.append(&open)?;
                tray_menu.append(&refresh)?;
                tray_menu.append(&quit)?;
                let tray = TrayIconBuilder::with_id("usage-monitor-poc")
                    .menu(&tray_menu)
                    .show_menu_on_left_click(false)
                    .tooltip("Usage Monitor")
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
                tray.set_tooltip(Some("Usage Monitor"))?;
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
                    show_popup_at(&window, 1000.0, 1000.0, None);
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
            let window = window.clone();
            let app = window.app_handle().clone();
            std::thread::spawn(move || {
                std::thread::sleep(Duration::from_millis(220));
                let intent_unchanged =
                    POPUP_INTENT_GENERATION.load(Ordering::SeqCst) == intent_generation;
                let focus_unchanged = FOCUS_GENERATION.load(Ordering::SeqCst) == focus_generation;
                if !REVEALING.load(Ordering::SeqCst)
                    && intent_unchanged
                    && focus_unchanged
                    && !window.is_focused().unwrap_or(false)
                    && window.is_visible().unwrap_or(false)
                {
                    let _ = app.run_on_main_thread(move || {
                        if window.is_visible().unwrap_or(false) {
                            let _ = hide_popup_native_window(&window);
                            LAST_FOCUS_HIDE_MS.store(unix_now_ms(), Ordering::SeqCst);
                        }
                    });
                }
            });
        })
        .run(tauri::generate_context!())
        .expect("error while running Usage Monitor Tauri POC");
}

#[cfg(test)]
mod tests {
    use super::{calculate_popup_position, LayoutRect};

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
}
