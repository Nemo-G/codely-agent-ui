# Unity Agent Client UI (Tauri)

This is the Tauri-based UI for the Unity Agent Client.

It can be launched by the Unity Editor extension in two ways:

- **IPC Sync (recommended)**: Unity publishes its window rect via shared memory; the Tauri app positions itself.
- **Embed (legacy, Windows-only)**: Unity embeds the external window via Win32 `SetParent`.

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

Windows helper scripts (use repo-bundled Wix/NSIS):

```powershell
cd tauri-ui
pwsh -File .\build-with-tools.ps1
# or MSI-only:
pwsh -File .\build-with-cache.ps1
```

The exe is typically under:

- `tauri-ui/src-tauri/target/release/tauri-ui.exe`

## Use with Unity (bundled binary)

1. Build the exe (see above).
2. Copy it to: `cn.tuanjie.codely.unity-agent-client-ui/Editor/Bin/win/tauri-ui.exe`
   - Or set `UNITY_AGENT_CLIENT_UI_EXE` to the full path of the exe **before launching Unity**.
3. In Unity, open: `Tools/Unity ACP Client (IPC Sync)`.

## Legacy embed mode (Windows)

Open: `Tools/Unity ACP Client` (embeds via `SetParent`).

## Recommended IDE Setup

- [VS Code](https://code.visualstudio.com/) + [Tauri](https://marketplace.visualstudio.com/items?itemName=tauri-apps.tauri-vscode) + [rust-analyzer](https://marketplace.visualstudio.com/items?itemName=rust-lang.rust-analyzer)
