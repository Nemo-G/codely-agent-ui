# Unity Agent Client UI (Web UI Host)

Unity Editor extension that hosts the web UI (`http://localhost:3999`) inside a dedicated Unity window.

It supports **two display modes**:

- **IPC Sync (default)**: no `SetParent`. Unity publishes the target screen-rect via shared memory and the **Tauri window moves itself** (target 60fps).
- **Embed (Legacy)**: Windows-only `SetParent` embedding.

## Default Behavior

Menu: `Tools > Unity ACP Client`

When opened, it will:

1. Start `codely serve web-ui`
2. Launch the UI window (prefer Tauri; fallback to browser app-mode)

## Bundled Tauri UI (no local build needed)

If you ship a prebuilt Tauri UI binary **inside this UPM package**, the Unity window will automatically prefer it (over browser app mode):

- Windows: put the exe in:
  - `cn.tuanjie.codely.unity-agent-client-ui/Editor/Bin/win/tauri-ui.exe`
  - (also accepted: `UnityAgentClientUI.exe` / `unity-agent-client-ui.exe`)
- macOS: put the app/binary in:
  - `cn.tuanjie.codely.unity-agent-client-ui/Editor/Bin/mac/` (name can be `tauri-ui` / `UnityAgentClientUI`)
- Linux: put the binary/AppImage in:
  - `cn.tuanjie.codely.unity-agent-client-ui/Editor/Bin/linux/` (name can be `tauri-ui` / `UnityAgentClientUI`)

Note:
- **Embed (Legacy)** is Windows-only.
- **IPC Sync** is designed to be cross-platform (file-backed shared memory + Tauri window positioning), but perfect “Unity-style z-order parenting” is currently only implemented on Windows (via Win32 owner HWND).

## Requirements

- Windows Editor
- Microsoft Edge or Google Chrome

## Optional

- `CODELY_WEB_UI_BROWSER`: set to the full path of your browser executable if it is not installed in a standard location.
- `UNITY_AGENT_CLIENT_UI_EXE` / `CODELY_UNITY_AGENT_CLIENT_UI_EXE`: full path to a Tauri UI exe to embed (preferred over browser app mode).

## IPC Sync env vars (set automatically by Unity)

- `UNITY_AGENT_CLIENT_UI_IPC_MODE=sync`
- `UNITY_AGENT_CLIENT_UI_IPC_PATH=<temp file path>` (file-backed shared memory; Tauri mmaps it)


