# Tuanjie Agent Client

Provides integration of any AI agent (Codely CLI, Gemini CLI, Claude Code, Codex CLI, etc.) with the Unity editor using Agent Client Protocol (ACP). Inspired by nuskey's UnityAgentClient package.

## Installation

This repo now ships **two** Unity packages:

- `cn.tuanjie.codely.agent-client`: legacy IMGUI ACP client window (chat UI)
- `cn.tuanjie.codely.unity-agent-client-ui`: Windows-only embedded Web UI window (default)

Copy both folders into your Unity project's `Packages/` directory (or add them via `Packages/manifest.json` using `file:` paths).

## Requirements

- Unity 2022.3 or later

## Setup

1. Configure the AI agent in `UserSettings/UnityAgentClientSettings.json` (auto-created on first open):
   - `Command`: the executable name of your AI agent
   - `Arguments`: command-line arguments (e.g. `--experimental-acp`)
   - `EnvironmentVariables`: API keys and other environment variables

2. Open the default embedded Web UI: `Tools/Unity ACP Client`

## Embedded Web UI (Windows)

On Windows Editor, `cn.tuanjie.codely.unity-agent-client-ui` will:

- start `codely serve web-ui`
- embed a browser app window that loads `http://127.0.0.1:3939` inside Unity

### Requirements

- Windows Editor
- Microsoft Edge or Google Chrome

### Optional environment override

- `CODELY_WEB_UI_BROWSER`: full path to your browser executable (if not in a standard install location)

## Legacy IMGUI Window

Open: `Tools/Unity ACP Client (Legacy IMGUI)`

## Core Components

- **AgentWindow.cs**: Main editor window for AI agent interaction
- **AgentSettings.cs**: Serializable settings data model
- **EditorMarkdownRenderer.cs**: Custom markdown renderer for Unity IMGUI
- **AgentClientProtocol/**: Vendored ACP protocol library

## License

MIT License