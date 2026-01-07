# Unity Agent Client — CODELY Reference

> **Unity tooling:** `unity_editor` CLI commands are not available in this environment. All context below is file-derived.

## Project Overview
- **Purpose:** Unity editor extension that embeds Agent Client Protocol (ACP) compatible AI agents inside the Unity Editor for contextual assistance.
- **Unity version:** 6000.2.7f2 (Unity 6 LTS stream).
- **Target platforms:** Editor tooling only (no standalone player build targets declared).
- **Render pipeline:** Universal Render Pipeline (URP 17.2.0) is enabled via `com.unity.render-pipelines.universal`.
- **Key packages & tech:**
  - `com.github-glitchenzo.nugetforunity` to fetch ACP .NET dependencies.
  - Unity Input System 1.14.2 (project dependency, although extension itself is editor-only).
  - Built-in MCP bridge implemented in C# (`BuiltinMcpServer.cs`) plus a Node.js stdio transport (`server.js`).
  - Other Unity defaults: Timeline, AI Navigation, UGUI, Multiplayer Center, IDE integrations.
- **External requirements:** Node.js runtime available on PATH for launching ACP-capable agents and the bundled MCP server.

## Entry Point
- Extension entry point is the custom editor window: **Window ▸ Unity Agent Client ▸ AI Agent** (`AgentWindow.cs`).

## Core Scripts
- `Editor/AgentWindow.cs`
  - Main ACP client window: manages connections, session lifecycle, chat UI, model/mode switching, tool calls, and asset attachments.
- `Editor/AgentSettingsProvider.cs`
  - Registers **Project ▸ Unity Agent Client** settings panel. Persists `UnityAgentClientSettings.json` under `UserSettings/` with command, arguments, env vars, and verbose logging flag.
- `Editor/AgentSettings.cs`
  - Serializable backing object for settings.
- `Editor/BuiltinMcpServer.cs`
  - Editor-initialized HTTP server (port `57123`) exposing `/tools` and `/read_unity_console` to ACP/MCP agents. Captures Unity console logs for the `read_unity_console` tool.
- `Editor/server.js`
  - Node.js stdio MCP server that forwards tool discovery/calls to the editor’s HTTP server.
- `Editor/EditorMarkdownRenderer.cs`
  - Markdown-to-IMGUI renderer for displaying agent responses with headings, lists, code blocks, etc.
- `Editor/Logger.cs` & `TaskExtensions.cs`
  - Verbose logging helper (tied to settings) and `Task.Forget()` extension for fire-and-forget async calls.
- `Editor/UnityAgentClient.Editor.asmdef`
  - Editor-only assembly definition keeping the extension isolated from runtime assemblies.

## Using in Unity
- **In-Editor Usage:**
  1. Install the package via Package Manager.
  2. Configure the desired ACP agent under **Project Settings ▸ Unity Agent Client** (command, arguments, env vars, optional verbose logging).
  3. Ensure Node.js is installed and accessible.
  4. Open **Window ▸ Unity Agent Client ▸ AI Agent** and press **Send** to start the agent session. The window spawns the agent process and attaches the built-in MCP bridge.
- **Built-in MCP server:** auto-starts when the editor loads via `BuiltinMcpServer`. The Node.js bridge (`server.js`) is resolved relative to the installed package.

## Development Conventions
- **Folder layout:**
  - All extension sources reside in `Editor/` (Editor-only).
  - No runtime assemblies or non-editor scripts.
- **Naming:** C# classes use `PascalCase`; serialized fields typically `camelCase` (Unity defaults). Assets follow Unity naming defaults (no enforced scheme documented).
- **Serialization & Config:** Settings written to `UserSettings/UnityAgentClientSettings.json` using `System.Text.Json` (indented formatting).
- **Async patterns:** Extensive use of async/await with custom `Forget()` to log exceptions.
- **UI:** IMGUI-based editor window with custom markdown renderer. Controls constrained to ~800px width.
- **Network exposure:** HTTP listener bound to localhost port 57123; ensure firewall permits local connections.

## Packages & Dependencies
- **Core:**
  - `com.unity.render-pipelines.universal` 17.2.0 (URP).
  - `com.unity.inputsystem` 1.14.2.
  - `com.unity.ai.navigation` 2.0.9.
  - `com.unity.timeline` 1.8.9, `com.unity.ugui` 2.0.0.
  - IDE plugins (`com.unity.ide.rider`, `com.unity.ide.visualstudio`).
  - `com.unity.test-framework` 1.6.0.
- **Third-party:** `com.github-glitchenzo.nugetforunity` from Git (for NuGet package management inside Unity editor).
- **Unity Modules:** All standard `com.unity.modules.*` enabled (audio, physics, UI, XR, etc.).

## Version Control Notes
- `.gitignore` already excludes Unity temp folders (`Library/`, `Temp/`, `Logs/`, etc.).
- Keep `Assets/UnityAgentClient/**`, `ProjectSettings/`, `Packages/manifest.json`, `Packages/packages-lock.json`, and docs under version control.
- Never commit `UserSettings/UnityAgentClientSettings.json` if it contains API keys or local paths—`.gitignore` should prevent this, but double-check before committing.

## TODO / Open Questions
- `AgentWindow.ConnectAsync` embeds a macOS-specific absolute path to `server.js`. Replace with a project-relative path (e.g., `Path.Combine(Application.dataPath, "UnityAgentClient/Editor/server.js")`) or expose via settings to support Windows/Linux.
- No automated tests or sample workflows demonstrate end-to-end ACP communication—consider adding editor tests or documentation covering typical agent sessions and tool invocations.
- Clarify intended URP usage; if the extension is editor-only, confirm whether URP prerequisites are necessary or can be stripped to simplify dependencies.
