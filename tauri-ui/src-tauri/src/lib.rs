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

    let _ = window.set_focus(); // best-effort: keep input working when it appears

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
        let mut last_visible: u32 = 0;
        let mut last_owner: i64 = 0;
        let mut tick: u32 = 0;

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
            if tick % 60 == 0 {
                ipc_log(&format!(
                    "[tauri][ipc] seq={} rect=({},{} {}x{}) visible={} owner={}",
                    seq1, x, y, w, h, visible, owner
                ));
            }

            #[cfg(windows)]
            {
                if hwnd != 0 {
                    use windows_sys::Win32::UI::WindowsAndMessaging::{
                        SetWindowLongPtrW, SetWindowPos, ShowWindow, GWLP_HWNDPARENT, SWP_NOACTIVATE,
                        SW_HIDE, SW_SHOW,
                    };
                    const HWND_TOPMOST: isize = -1;

                    if owner != 0 && owner != last_owner {
                        last_owner = owner;
                        unsafe {
                            let _ = SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, owner as isize);
                        }
                        ipc_log(&format!("[tauri][ipc] set owner={}", owner));
                    }

                    if visible != last_visible {
                        last_visible = visible;
                        unsafe {
                            ShowWindow(hwnd, if visible != 0 { SW_SHOW } else { SW_HIDE });
                        }
                        ipc_log(&format!("[tauri][ipc] show={}", visible));
                    }

                    if visible != 0 && w > 0 && h > 0 {
                        unsafe {
                            // Aggressive: keep the window top-most so it never disappears behind other editor windows.
                            // This is intentionally stronger than owner-only z-order.
                            SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE);
                        }
                        if tick % 60 == 0 {
                            ipc_log("[tauri][ipc] SetWindowPos");
                        }
                    }

                    continue;
                }
            }

            // Fallback: use Tauri cross-platform window APIs.
            if visible != last_visible {
                last_visible = visible;
                if visible != 0 {
                    let _ = window.show();
                } else {
                    let _ = window.hide();
                }
            }

            if visible != 0 && w > 0 && h > 0 {
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
    let url_str = env::var("UNITY_AGENT_CLIENT_UI_URL").unwrap_or_else(|_| "http://localhost:3999".to_string());
    let url = url_str.parse().unwrap();

    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(move |app| {
            // Create a single main window that loads the external web UI.
            let w = WebviewWindowBuilder::new(app, "main".to_string(), WebviewUrl::External(url))
                .title("Unity Agent Client")
                .inner_size(800.0, 600.0)
                .decorations(false)
                .resizable(true)
                .skip_taskbar(true)
                .build()?;

            maybe_start_ipc_sync(w.clone());
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
