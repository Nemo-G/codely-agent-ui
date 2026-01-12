# Unity Agent Client UI (Web UI Host)

Unity Editor extension that hosts the web UI (`http://127.0.0.1:3939`) inside a dedicated Unity window.

It supports **two display modes**:

- **IPC Sync (recommended)**: no `SetParent`. Unity publishes the target screen-rect via shared memory and the **Tauri window moves itself** (target 60fps).
- **Embed (legacy)**: Windows-only `SetParent` embedding.

## Open the UI

- Recommended: `Tools > Unity ACP Client (IPC Sync)`
- Legacy: `Tools > Unity ACP Client` (Windows-only embed)

When opened, it will:

1. Start `codely serve web-ui`
2. Launch the UI window (prefer Tauri; fallback to browser app-mode)

## Bundle a prebuilt Tauri UI (no local build needed for users)

If you ship a prebuilt Tauri UI binary **inside this UPM package**, the Unity window will automatically prefer it (over browser app mode):

- Windows: put the exe in:
  - `cn.tuanjie.codely.unity-agent-client-ui/Editor/Bin/win/tauri-ui.exe`
  - (also accepted: `UnityAgentClientUI.exe` / `unity-agent-client-ui.exe`)
- macOS: put the app/binary in:
  - `cn.tuanjie.codely.unity-agent-client-ui/Editor/Bin/mac/` (name can be `tauri-ui` / `UnityAgentClientUI`)
- Linux: put the binary/AppImage in:
  - `cn.tuanjie.codely.unity-agent-client-ui/Editor/Bin/linux/` (name can be `tauri-ui` / `UnityAgentClientUI`)

## Build the Tauri UI (developer workflow)

From the repo root:

```bash
cd tauri-ui
npm install

# Windows (recommended; uses repo-bundled Wix/NSIS)
pwsh -File build-with-tools.ps1

# macOS/Linux (or if you already have toolchain installed)
npm run tauri build
```

Then copy the built binary into this UPM package:

- Windows: copy `tauri-ui/src-tauri/target/release/tauri-ui.exe` to `cn.tuanjie.codely.unity-agent-client-ui/Editor/Bin/win/tauri-ui.exe`

Tip: during local dev, you can skip copying by setting `UNITY_AGENT_CLIENT_UI_EXE` (or `CODELY_UNITY_AGENT_CLIENT_UI_EXE`) to the full path of your built exe **before launching Unity**.

## Optional (Windows): native window-sync plugin for smooth dragging

`CodelyUnityAgentClientUIWindowSync.dll` is an optional native plugin used by **IPC Sync** mode to publish the host rect even while Unity's UI thread is blocked (e.g. docking/dragging).
If it's missing, IPC Sync will fall back to C# polling (still works, less smooth).

Build it (requires CMake + MSVC) **outside** this UPM package folder, then copy only the built DLL into this package.

**Important:** do **NOT** build into `cn.tuanjie.codely.unity-agent-client-ui/Editor/WindowSync/native/` or any subfolder (Unity will scan/import build outputs and may crash by treating `*.obj` as a 3D model).

Example (repo root, out-of-tree build dir):

```bash
cmake -S cn.tuanjie.codely.unity-agent-client-ui/Editor/WindowSync/native -B .build/WindowSync/win64-release -G "Visual Studio 17 2022" -A x64
cmake --build .build/WindowSync/win64-release --config Release
```

Then copy `CodelyUnityAgentClientUIWindowSync.dll` to:
- `cn.tuanjie.codely.unity-agent-client-ui/Editor/Bin/win/`

Note: `.build/` is generated and should not be committed.

Note:
- **Embed (Legacy)** is Windows-only.
- **IPC Sync** is designed to be cross-platform (file-backed shared memory + Tauri window positioning), but perfect “Unity-style z-order parenting” is currently only implemented on Windows (via Win32 owner HWND).

## Requirements

- Unity 2022.3+ Editor
- Windows Editor for Embed mode
- Microsoft Edge or Google Chrome (browser fallback)

## Optional

- `CODELY_WEB_UI_BROWSER`: set to the full path of your browser executable if it is not installed in a standard location.
- `UNITY_AGENT_CLIENT_UI_EXE` / `CODELY_UNITY_AGENT_CLIENT_UI_EXE`: full path to a Tauri UI exe to embed (preferred over browser app mode).

## IPC Sync env vars (set automatically by Unity)

- `UNITY_AGENT_CLIENT_UI_IPC_MODE=sync`
- `UNITY_AGENT_CLIENT_UI_IPC_PATH=<temp file path>` (file-backed shared memory; Tauri mmaps it)


