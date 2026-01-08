# Tuanjie Agent Client - Package Documentation

## 📋 Package Overview

**Tuanjie Agent Client** (`cn.tuanjie.codely.agent-client`) is an editor extension for Unity that enables integration of any AI agent (Codely CLI, Gemini CLI, Claude Code, Codex CLI, opencode, Goose, etc.) with the Unity Editor using the Agent Client Protocol (ACP). This package allows AI agents to interact with Unity projects as context, enabling developers to leverage AI for project understanding, issue identification, and workflow optimization.

> **Note:** This package is inspired by nuskey's UnityAgentClient package and is now an independent package maintained by Tuanjie.

### Key Information
- **Package ID:** `cn.tuanjie.codely.agent-client`
- **Version:** 0.1.0
- **Display Name:** Codely Agent Client
- **Author:** codely
- **Minimum Unity Version:** 2022.3
- **License:** MIT License
- **Dependencies:** `com.unity.modules.ui` (1.0.0+), `com.unity.nuget.newtonsoft-json` (3.2.1)
- **Protocol SDK:** `AgentClientProtocol` (vendored source under `Editor/AgentClientProtocol/`)

## 🎯 Purpose & Use Cases

### Primary Purpose
Provides a bridge between AI agents and the Unity Editor, allowing AI to access Unity project structure, assets, and console logs while working within the editor environment.

### Recommended Use Cases
- **Project Understanding**: Use AI to analyze the entire Unity project as a document
- **Issue Identification**: Help identify bottlenecks and problems in the codebase
- **Workflow Optimization**: AI-assisted development without leaving the Unity Editor
- **Team Collaboration**: Enable team members to better understand project architecture

### ⚠️ Not Recommended For
- **Code Editing Within Editor**: Due to Unity's Domain Reload behavior, editing C# scripts will disconnect the session. Use AI agents integrated into IDEs or code editors for coding tasks.

## 🏗️ Architecture & Components

### Core Components

#### AgentWindow (Editor Window)
- **Location**: `Editor/AgentWindow.cs`
- **Purpose**: Main UI window for AI agent interaction
- **Key Features**:
  - Chat interface with markdown rendering
  - Asset drag-and-drop for context attachment
  - Model and mode switching
  - Permission request handling
  - Authentication method selection
  - Session management (connect/disconnect/stop)
- **Access**: `Tools > Unity ACP Client (Legacy IMGUI)`

#### AgentSettingsProvider (Settings Provider)
- **Location**: `Editor/AgentSettingsProvider.cs`
- **Purpose**: Project settings configuration for AI agent
- **Settings Path**: `Project Settings > Unity Agent Client`
- **Configuration Fields**:
  - `Command`: AI agent executable command (e.g., `gemini`, `opencode`)
  - `Arguments`: Command-line arguments (e.g., `--experimental-acp`, `acp`)
  - `Environment Variables`: Dictionary of environment variables (e.g., API keys)
  - `Verbose Logging`: Enable detailed debug output
- **Storage**: JSON file in `UserSettings/UnityAgentClientSettings.json`

#### AgentSettings (Data Model)
- **Location**: `Editor/AgentSettings.cs`
- **Purpose**: Serializable settings container
- **Namespace**: `UnityAgentClient`

#### MCP Integration (Optional)
- This package does **not** ship a built-in MCP server/bridge anymore.
- If your agent already provides Unity tools (e.g., Codely CLI), no extra MCP setup is required on the Unity side.

#### EditorMarkdownRenderer (UI Component)
- **Location**: `Editor/EditorMarkdownRenderer.cs`
- **Purpose**: Custom markdown renderer for Unity IMGUI
- **Supported Syntax**:
  - Headings (H1-H6)
  - Code blocks with language specification
  - Inline code
  - Bold, italic, strikethrough
  - Ordered and unordered lists
  - Block quotes
  - Horizontal rules
  - Links (text-only display)

#### Logger (Utility)
- **Location**: `Editor/Logger.cs`
- **Purpose**: Centralized logging for the package
- **Prefix**: `[UnityAgentClient]`
- **Levels**: Verbose (conditional), Error, Warning

#### TaskExtensions (Utility)
- **Location**: `Editor/TaskExtensions.cs`
- **Purpose**: Fire-and-forget task execution with exception handling
- **Method**: `Task.Forget()` - Safely ignore task completion

#### IsExternalInit (Compiler Polyfill)
- **Location**: `Editor/IsExternalInit.cs`
- **Purpose**: Enables C# 9.0 `init` properties in older Unity versions
- **Namespace**: `System.Runtime.CompilerServices`

### Protocol Integration

#### AgentClientProtocol (vendored source)
- **Location**: `Editor/AgentClientProtocol/`
- **Protocol**: Agent Client Protocol (ACP) v1
- **Key Interfaces**:
  - `IAcpClient` - Client-side ACP implementation
  - `ClientSideConnection` - Connection management
  - `SessionUpdate` - Session events
  - Various request/response types
- **Note**: Source-only. No bundled `AgentClientProtocol.dll` dependency.

#### JSON (Newtonsoft.Json)
- **Source**: Unity Package Manager dependency: `com.unity.nuget.newtonsoft-json`
- **Purpose**: JSON serialization/deserialization for ACP JSON-RPC and settings

## 📁 Package Structure

```
cn.tuanjie.codely.agent-client/
├── Editor/
│   ├── AgentClientProtocol/             # ACP protocol library (vendored source)
│   ├── AgentSettings.cs                 # Settings data model
│   ├── AgentSettingsProvider.cs         # Settings provider UI
│   ├── AgentWindow.cs                   # Main editor window
│   ├── EditorMarkdownRenderer.cs        # Markdown UI renderer
│   ├── IsExternalInit.cs                # C# 9.0 polyfill
│   ├── Logger.cs                        # Logging utility
│   ├── TaskExtensions.cs                # Async utilities
│   └── UnityAgentClient.Editor.asmdef   # Assembly definition
├── docs/
│   └── images/
│       ├── img-agent-window.png         # UI screenshot
│       └── img-demo.gif                  # Demo animation
├── CODELY.md                            # This documentation file
├── LICENSE                              # MIT License
├── package.json                         # Package manifest
└── README.md                            # Documentation
```

## 🚀 Installation & Setup

### Installation

#### From Disk (Manual)
Copy the `cn.tuanjie.codely.agent-client` folder to your Unity project's `Packages/` directory.

### Prerequisites
- **Unity Editor**: 2021.3 or later (package specifies 2022.3, but README says 2021.3+)

### Configuration

1. **Open Project Settings**:
   - Navigate to `Edit > Project Settings > Unity Agent Client`

2. **Configure AI Agent**:
   - **Command**: Enter the executable name (e.g., `gemini`, `opencode`, `goose`)
   - **Arguments**: Enter command-line arguments (e.g., `--experimental-acp`, `acp`)
   - **Environment Variables**: Add API keys or other environment variables as key-value pairs
   - **Verbose Logging**: Enable for debug output

3. **macOS PATH Resolution**:
   - If PATH resolution fails in zsh, use `which` to find the full path:
     ```bash
     which gemini
     # /usr/local/bin/gemini
     ```
   - Enter the full path in the Command field

### Supported AI Agents

#### Gemini CLI (Experimental)
- **Command**: `gemini`
- **Arguments**: `--experimental-acp`
- **Environment Variables**: `GEMINI_API_KEY` (if using API key login)

#### Claude Code (via Zed Adapter)
- **Command**: `claude-code-acp`
- **Arguments**: (empty)
- **Setup**: Follow https://github.com/zed-industries/claude-code-acp

#### Codex CLI (via Zed Adapter)
- **Command**: `codex-acp`
- **Arguments**: (empty)
- **Setup**: Follow https://github.com/zed-industries/codex-acp

#### opencode (Recommended)
- **Command**: `opencode`
- **Arguments**: `acp`
- **Info**: Open-source AI agent supporting any LLM provider, MCP, and ACP
- **Website**: https://opencode.ai/

#### Goose
- **Command**: `goose`
- **Arguments**: `acp`
- **Info**: Open-source AI agent for CLI/desktop
- **Website**: https://block.github.io/goose/

## 🎮 Usage

### Opening the AI Agent Window
1. Default (Web UI): `Tools > Unity ACP Client` (requires `cn.tuanjie.codely.unity-agent-client-ui`)
2. Legacy (IMGUI): `Tools > Unity ACP Client (Legacy IMGUI)`

### Sending Prompts
1. Enter your prompt in the text input field
2. Optionally drag and drop assets from the Project view to attach as context
3. Click **Send** to submit the prompt
4. The AI agent will respond with markdown-formatted text

### Attaching Assets
- **Drag and Drop**: Drag assets from Project view or Scene hierarchy into the window
- **Supported Asset Types**: Any Unity object (prefabs, scripts, materials, textures, etc.)
- **Context**: Assets are converted to file:// URIs and sent as resource links

### Model & Mode Selection
- **Model**: Select from available AI models (dropdown on the right)
- **Mode**: Select from available agent modes (dropdown on the left)
- Changes take effect immediately for subsequent requests

### Stopping Running Operations
- Click **Stop** button to cancel the current AI operation
- Useful for long-running tasks or if you want to modify the prompt

### Permission Handling
- Some AI agents may request permission before executing tools
- Options typically include:
  - **Allow**: Execute the tool once
  - **Allow Always**: Always allow this tool
  - **Reject**: Reject the tool execution
  - **Reject Always**: Always reject this tool

### Authentication
- If the AI agent requires authentication, a selection dialog will appear
- Choose the appropriate authentication method from the list
- Follow the agent's authentication flow

## 🔧 Development Conventions

### Code Organization
- **Namespace**: `UnityAgentClient` for all classes
- **Editor-Only Code**: All code is in `Editor/` folder
- **Assembly Definition**: `UnityAgentClient.Editor.asmdef` (Editor-only)
- **External Dependencies**: `com.unity.nuget.newtonsoft-json`

### Naming Standards
- **Classes**: PascalCase (`AgentWindow`, `AgentSettings`)
- **Methods**: PascalCase (`ConnectAsync`, `SendRequestAsync`)
- **Fields**: camelCase (`connectionStatus`, `inputText`)
- **Constants**: UPPER_CASE (`Port`, `ConfigFileName`)
- **Private Fields**: camelCase with underscore prefix (rarely used)

### Architecture Patterns
- **Singleton Pattern**: Not used (window instance managed by Unity)
- **Settings Provider Pattern**: `SettingsProvider` for project settings
- **Async/Await**: Extensive use of async operations
- **Event-Driven**: Unity events for assembly reload, application quit
- **Protocol-Based**: Strict adherence to ACP

### UI/UX Patterns
- **IMGUI**: Uses Unity's immediate mode GUI (`EditorGUILayout`)
- **Custom Styles**: Custom GUIStyle instances for markdown rendering
- **Foldouts**: Collapsible sections for tool calls, plans, etc.
- **Scroll Views**: For conversation history and input fields
- **Real-time Updates**: `OnInspectorUpdate()` for UI refresh

### Error Handling
- **Try-Catch**: Async operations wrapped in try-catch blocks
- **Logging**: Centralized logging via `Logger` class
- **User Feedback**: Error messages displayed in Unity Console
- **Graceful Degradation**: Features fail safely if unavailable

## 🧩 Tool Integration

This package is an **ACP client UI** running inside Unity Editor.
Any “tools” beyond ACP standard capabilities are expected to be provided by your agent (e.g., Codely CLI).

## 📦 Package Manifest (package.json)

```json
{
  "name": "cn.tuanjie.codely.agent-client",
  "version": "0.1.0",
  "displayName": "Tuanjie Agent Client",
  "description": "Provides integration of any AI agent (Codely CLI, Gemini CLI, Claude Code, Codex CLI, etc.) with the Unity editor using Agent Client Protocol(ACP). Inspired by nuskey's UnityAgentClient package.",
  "author": {
    "name": "codely"
  },
  "unity": "2022.3",
  "unityRelease": "0f1",
  "type": "module",
  "dependencies": {
    "com.unity.modules.ui": "1.0.0",
    "com.unity.nuget.newtonsoft-json": "3.2.1"
  }
}
```

## 🔐 Security Considerations

### API Keys & Secrets
- Settings are stored in `UserSettings/UnityAgentClientSettings.json`
- The `UserSettings/` folder is typically excluded by `.gitignore`
- **Warning**: Be careful not to accidentally commit API keys
- **Recommendation**: Use environment variables instead of hardcoding keys

### File System Access
- The package can read and write text files via ACP
- File operations are limited to the project directory
- Write operations create directories if they don't exist

### Network Access
- AI agents run as separate processes with their own network access
- No external network access is required by the package itself

## 🐛 Troubleshooting

### Common Issues

#### "No agent has been configured"
- **Cause**: No AI agent command specified in settings
- **Solution**: Open `Project Settings > Unity Agent Client` and configure the Command field

#### Connection Failed
- **Cause**: AI agent not installed or not in PATH
- **Solution**: 
  - Verify the agent is installed
  - Check the command path (use full path on macOS if needed)
  - Test the command in terminal first

#### macOS PATH Resolution Issues
- **Cause**: zsh PATH not properly resolved
- **Solution**: Use `which <command>` to find full path and enter it in Command field

#### Domain Reload Disconnects Session
- **Cause**: Editing C# scripts triggers Unity's domain reload
- **Solution**: This is expected behavior. Use IDE-integrated AI for coding tasks

#### Newtonsoft.Json Not Resolved
- **Cause**: Unity can't fetch `com.unity.nuget.newtonsoft-json` (UPM registry issue / offline environment)
- **Solution**:
  - Ensure Unity Package Manager can access the registry
  - Or add `com.unity.nuget.newtonsoft-json` to your project `Packages/manifest.json`

### Debug Mode
Enable verbose logging in settings to see detailed debug information:
1. Open `Project Settings > Unity Agent Client`
2. Check **Verbose Logging**
3. Check Unity Console for `[UnityAgentClient]` prefixed messages

## 📚 Protocol References

### Agent Client Protocol (ACP)
- **Specification**: https://agentclientprotocol.com
- **Version**: 1
- **Transport**: JSON-RPC 2.0 over stdio
- **Supported Agents**: https://agentclientprotocol.com/overview/agents

### Model Context Protocol (MCP)
- **Specification**: https://modelcontextprotocol.io
- **Version**: 2024-11-05
- **Transport**: JSON-RPC 2.0 over stdio
- **Purpose**: Standardized tool interface for AI agents

## 🤝 Contributing

This package is open-source under the MIT License.

## 📄 License

**MIT License**

See LICENSE file for details.

## 🔗 External Resources

- **ACP Specification**: https://agentclientprotocol.com
- **Zed ACP Adapters**: https://github.com/zed-industries
- **opencode**: https://opencode.ai/
- **Goose**: https://block.github.io/goose/

## 📝 Changelog

### Version 0.1.0
- Initial release
- Support for ACP v1
- Built-in MCP server with Unity console log tool
- Markdown rendering for AI responses
- Asset drag-and-drop for context
- Model and mode switching
- Permission request handling
- Authentication method selection

## ⚠️ Known Limitations

1. **Domain Reload**: Editing C# scripts triggers domain reload, disconnecting the session
2. **Terminal Tools**: Not implemented (CreateTerminal, TerminalOutput, etc.)
3. **Ext Methods**: Not implemented (ExtMethod, ExtNotification)
4. **macOS PATH**: May require full path specification for agent commands
5. **Single Session**: Only one AI agent session at a time
6. **Console Log Limitation**: Limited to last N logs (configurable, default 100)

## 🔮 Future Enhancements

### Potential Features
- [ ] Terminal tool implementation
- [ ] Ext method support for custom Unity commands
- [ ] Multiple simultaneous sessions
- [ ] Scene-specific context attachment
- [ ] Built-in code review tools
- [ ] Git integration for diff viewing
- [ ] Custom MCP server configuration
- [ ] Session history and export
- [ ] Streaming responses (if supported by agent)

---

*Last Updated: January 6, 2026*
*Document Version: 1.0*
*Maintainer: Codely CLI*