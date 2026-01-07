# Unity Agent Client UI (Embedded Web UI)

Windows-only Unity Editor extension that embeds a browser app window inside a Unity `EditorWindow`.

## Default Behavior

Menu: `Tools > Unity ACP Client`

When opened, it will:

1. Start `codely serve web-ui`
2. Embed a browser window that loads `http://localhost:3999`

## Requirements

- Windows Editor
- Microsoft Edge or Google Chrome

## Optional

- `CODELY_WEB_UI_BROWSER`: set to the full path of your browser executable if it is not installed in a standard location.
- `UNITY_AGENT_CLIENT_UI_EXE` / `CODELY_UNITY_AGENT_CLIENT_UI_EXE`: full path to a Tauri UI exe to embed (preferred over browser app mode).


