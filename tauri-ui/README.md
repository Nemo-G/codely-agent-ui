# Unity Agent Client UI (Tauri)

This is the Tauri-based UI for the Unity Agent Client. It is intended to be **embedded inside a Unity `EditorWindow`**
on Windows (so it feels like part of the editor), via Win32 `SetParent`.

## Requirements

- Node.js + npm
- Rust toolchain (`cargo`, `rustc`)
- Windows: WebView2 Runtime (usually already installed)

## Run (dev)

```bash
cd tauri-ui
npm install
npm run tauri dev
```

## Build (release exe)

```bash
cd tauri-ui
npm install
npm run tauri build
```

The exe is typically under:

- `tauri-ui/src-tauri/target/release/tauri-ui.exe`

## Embed into Unity EditorWindow (Windows)

1. Build the exe (see above).
2. In your Unity project, edit `UserSettings/UnityAgentClientSettings.json` and set:

```json
{
  "UseEmbeddedTauriUi": true,
  "UiCommand": "F:\\UnityAgentClient\\tauri-ui\\src-tauri\\target\\release\\tauri-ui.exe",
  "UiArguments": "",
  "KillUiOnClose": true
}
```

3. In Unity, open: `Tools/Unity ACP Client (Tauri Embedded)`.

## Recommended IDE Setup

- [VS Code](https://code.visualstudio.com/) + [Tauri](https://marketplace.visualstudio.com/items?itemName=tauri-apps.tauri-vscode) + [rust-analyzer](https://marketplace.visualstudio.com/items?itemName=rust-lang.rust-analyzer)
