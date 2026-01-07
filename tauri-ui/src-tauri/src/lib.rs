// Learn more about Tauri commands at https://tauri.app/develop/calling-rust/
use serde_json::json;
use std::env;
use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;

#[tauri::command]
fn greet(name: &str) -> String {
    format!("Hello, {}! You've been greeted from Rust!", name)
}

#[tauri::command]
fn unity_ping() -> Result<serde_json::Value, String> {
    let host = env::var("UNITY_ACP_BRIDGE_HOST").unwrap_or_else(|_| "127.0.0.1".to_string());
    let port = env::var("UNITY_ACP_BRIDGE_PORT").map_err(|_| "UNITY_ACP_BRIDGE_PORT not set".to_string())?;
    let addr = format!("{}:{}", host, port);

    let mut stream = TcpStream::connect(addr).map_err(|e| e.to_string())?;
    let _ = stream.set_nodelay(true);

    let req = json!({
        "jsonrpc": "2.0",
        "id": 1,
        "method": "bridge.ping",
        "params": {}
    });

    stream
        .write_all(req.to_string().as_bytes())
        .and_then(|_| stream.write_all(b"\n"))
        .map_err(|e| e.to_string())?;

    let mut reader = BufReader::new(stream);
    let mut line = String::new();
    reader.read_line(&mut line).map_err(|e| e.to_string())?;

    let value: serde_json::Value = serde_json::from_str(&line).map_err(|e| e.to_string())?;
    Ok(value)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![greet, unity_ping])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
