use std::env;
use std::fs::OpenOptions;
use std::path::PathBuf;
use std::io::Write;
use std::time::Duration;
use tauri::{webview::WebviewWindowBuilder, WebviewUrl};

const IPC_MAGIC: u32 = 0x3149_5543; // "CUI1" little-endian
const IPC_VERSION: u32 = 1;
const IPC_SIZE: usize = 64;

fn read_u32_le(buf: &[u8], offset: usize) -> u32 {
    let b = [buf[offset], buf[offset + 1], buf[offset + 2], buf[offset + 3]];
    u32::from_le_bytes(b)
}

fn read_i32_le(buf: &[u8], offset: usize) -> i32 {
    let b = [buf[offset], buf[offset + 1], buf[offset + 2], buf[offset + 3]];
    i32::from_le_bytes(b)
}

fn read_i64_le(buf: &[u8], offset: usize) -> i64 {
    let b = [
        buf[offset],
        buf[offset + 1],
        buf[offset + 2],
        buf[offset + 3],
        buf[offset + 4],
        buf[offset + 5],
        buf[offset + 6],
        buf[offset + 7],
    ];
    i64::from_le_bytes(b)
}

fn ipc_debug_enabled() -> bool {
    match env::var("UNITY_AGENT_CLIENT_UI_IPC_DEBUG") {
        Ok(v) => {
            let v = v.to_lowercase();
            v == "1" || v == "true" || v == "yes" || v == "y"
        }
        Err(_) => false,
    }
}

fn ipc_log_path() -> PathBuf {
    let pid = std::process::id();
    std::env::temp_dir().join(format!("CodelyUnityAgentClientUI_ipc_{}.log", pid))
}

fn ipc_log(msg: &str) {
    if !ipc_debug_enabled() {
        return;
    }

    // Best-effort; never crash the UI for logging.
    if let Ok(mut f) = OpenOptions::new().create(true).append(true).open(ipc_log_path()) {
        let _ = writeln!(f, "{}", msg);
    }
}

fn maybe_start_ipc_sync(window: tauri::WebviewWindow) {
    let mode = env::var("UNITY_AGENT_CLIENT_UI_IPC_MODE").unwrap_or_default();
    if mode.to_lowercase() != "sync" {
        ipc_log(&format!("[tauri][ipc] disabled: UNITY_AGENT_CLIENT_UI_IPC_MODE='{}'", mode));
        return;
    }

    // Prefer *_PATH (file-backed mapping), fallback to *_NAME for older Unity package builds.
    let path = env::var("UNITY_AGENT_CLIENT_UI_IPC_PATH")
        .or_else(|_| env::var("UNITY_AGENT_CLIENT_UI_IPC_NAME"))
        .ok();
    let Some(path) = path else {
        ipc_log("[tauri][ipc] missing UNITY_AGENT_CLIENT_UI_IPC_PATH/NAME");
        return;
    };
    ipc_log(&format!("[tauri][ipc] mode=sync path={}", path));
    // IMPORTANT:
    // - Do NOT steal focus on startup (Unity should remain foreground).
    // - We'll show the window only after we have a valid rect from IPC, positioned first.

    std::thread::spawn(move || {
        let file = match OpenOptions::new().read(true).open(&path) {
            Ok(f) => f,
            Err(e) => {
                ipc_log(&format!("[tauri][ipc] failed to open mapping file: {}", e));
                return;
            }
        };

        let mmap = unsafe {
            match memmap2::MmapOptions::new().len(IPC_SIZE).map(&file) {
                Ok(m) => m,
                Err(e) => {
                    ipc_log(&format!("[tauri][ipc] failed to mmap: {}", e));
                    return;
                }
            }
        };

        let mut last_seq: u32 = 0;
        let mut last_owner: i64 = 0;
        let mut tick: u32 = 0;
        let mut is_shown: bool = false;
        let mut last_active: u32 = 0;

        #[cfg(windows)]
        let hwnd: isize = match window.hwnd() {
            Ok(h) => h.0 as isize,
            Err(_) => 0,
        };
        #[cfg(windows)]
        ipc_log(&format!("[tauri][ipc] hwnd=0x{:X}", hwnd));

        loop {
            // 60 fps target
            std::thread::sleep(Duration::from_millis(16));
            tick = tick.wrapping_add(1);

            let buf: &[u8] = &mmap[..];
            if buf.len() < IPC_SIZE {
                continue;
            }

            if read_u32_le(buf, 0) != IPC_MAGIC || read_u32_le(buf, 4) != IPC_VERSION {
                if tick % 120 == 0 {
                    ipc_log(&format!(
                        "[tauri][ipc] bad header: magic=0x{:X} ver={}",
                        read_u32_le(buf, 0),
                        read_u32_le(buf, 4)
                    ));
                }
                continue;
            }

            // Two-phase read: seq before + after to avoid torn reads
            let seq1 = read_u32_le(buf, 8);
            let x = read_i32_le(buf, 12);
            let y = read_i32_le(buf, 16);
            let w = read_i32_le(buf, 20);
            let h = read_i32_le(buf, 24);
            let flags = read_u32_le(buf, 32);
            let owner = read_i64_le(buf, 40);
            let seq2 = read_u32_le(buf, 8);

            if seq1 != seq2 || seq1 == 0 || seq1 == last_seq {
                continue;
            }
            last_seq = seq1;

            let visible = flags & 1;
            let active = flags & 2;
            if tick % 60 == 0 {
                ipc_log(&format!(
                    "[tauri][ipc] seq={} rect=({},{} {}x{}) visible={} active={} owner={}",
                    seq1, x, y, w, h, visible, active, owner
                ));
            }

            #[cfg(windows)]
            {
                if hwnd != 0 {
                    use windows_sys::Win32::UI::WindowsAndMessaging::{
                        SetWindowLongPtrW, SetWindowPos, ShowWindow, GWLP_HWNDPARENT, SWP_NOACTIVATE, SWP_NOZORDER,
                        SWP_NOMOVE, SWP_NOSIZE, SW_HIDE, SW_SHOWNOACTIVATE,
                    };
                    const HWND_TOP: isize = 0;
                    const HWND_TOPMOST: isize = -1;
                    const HWND_NOTOPMOST: isize = -2;

                    if owner != last_owner {
                        last_owner = owner;
                        unsafe {
                            let _ = SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, owner as isize);
                        }
                        ipc_log(&format!("[tauri][ipc] set owner={}", owner));
                    }

                    // Only show once we have a valid rect. This prevents the "flash at (0,0) then move" on startup.
                    let want_show = visible != 0 && w > 0 && h > 0;
                    let want_topmost = active != 0;
                    if want_show && !is_shown {
                        unsafe {
                            // Position first (still hidden), then show without activating Unity focus.
                            let insert_after = if want_topmost { HWND_TOPMOST } else { HWND_TOP };
                            let flags = if want_topmost { SWP_NOACTIVATE } else { SWP_NOACTIVATE | SWP_NOZORDER };
                            SetWindowPos(hwnd, insert_after, x, y, w, h, flags);
                            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                        }
                        is_shown = true;
                        ipc_log("[tauri][ipc] show=1 (no-activate)");
                    } else if !want_show && is_shown {
                        unsafe {
                            // Ensure we don't remain topmost when hidden.
                            if last_active != 0 {
                                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                            }
                            ShowWindow(hwnd, SW_HIDE);
                        }
                        is_shown = false;
                        ipc_log("[tauri][ipc] show=0");
                    }

                    if want_show {
                        unsafe {
                            if want_topmost {
                                // User requested: when active, keep it at the very top (but we hide when Unity isn't active).
                                SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE);
                            } else {
                                // When not active, DO NOT fight z-order; only move/size.
                                // This avoids "always on top" within Unity when other editor windows are focused.
                                if last_active != 0 {
                                    SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                                }
                                SetWindowPos(hwnd, HWND_TOP, x, y, w, h, SWP_NOACTIVATE | SWP_NOZORDER);
                            }
                        }
                        last_active = active;
                        if tick % 60 == 0 {
                            ipc_log("[tauri][ipc] SetWindowPos");
                        }
                    }

                    continue;
                }
            }

            // Fallback: use Tauri cross-platform window APIs.
            let want_show = visible != 0 && w > 0 && h > 0;
            if want_show && !is_shown {
                let _ = window.show();
                is_shown = true;
            } else if !want_show && is_shown {
                let _ = window.hide();
                is_shown = false;
            }

            if want_show {
                let _ = window.set_position(tauri::Position::Physical(tauri::PhysicalPosition { x, y }));
                let _ = window.set_size(tauri::Size::Physical(tauri::PhysicalSize {
                    width: w as u32,
                    height: h as u32,
                }));
            }
        }
    });
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    // codely serve web-ui defaults to 3939 (unless --port is specified)
    let url_str = env::var("UNITY_AGENT_CLIENT_UI_URL").unwrap_or_else(|_| "http://127.0.0.1:3939".to_string());
    let url = url_str.parse().unwrap();
    let is_sync = env::var("UNITY_AGENT_CLIENT_UI_IPC_MODE")
        .map(|v| v.to_lowercase() == "sync")
        .unwrap_or(false);

    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(move |app| {
            // Create a single main window that loads the external web UI.
            let mut b = WebviewWindowBuilder::new(app, "main".to_string(), WebviewUrl::External(url))
                .title("Unity Agent Client")
                .inner_size(800.0, 600.0)
                .decorations(false)
                // In IPC sync mode, Unity owns the size. Disabling native resize hit-testing prevents
                // the tauri window from "fighting" Unity when the user drags Unity's borders.
                .resizable(!is_sync)
                .skip_taskbar(true);

            // IPC Sync: start hidden to avoid "flash then move". We'll show once IPC publishes a valid rect.
            if is_sync {
                b = b.visible(false).focused(false);
            }

            let w = b.build()?;

            maybe_start_ipc_sync(w.clone());
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
