using UnityEditor;
using UnityEngine;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AgentClientProtocol;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System;
using System.Text;
using Newtonsoft.Json.Linq;

namespace UnityAgentClient
{
    public sealed class AgentWindow : EditorWindow, IAcpClient
    {
        enum ConnectionStatus
        {
            Pending,
            Success,
            Failed,
        }

        static GUIStyle wordWrapTextAreaStyle;

        Vector2 conversationScroll;
        Vector2 inputScroll;
        string inputText = "";
        readonly List<SessionUpdate> messages = new();
        readonly Dictionary<string, bool> foldoutStates = new();
        readonly List<UnityEngine.Object> attachedAssets = new();

        // UI updates can arrive from background JSON-RPC threads. We queue them and apply only during the Layout event
        // to avoid IMGUI "Invalid GUILayout state" errors (data changing between Layout/Repaint).
        readonly ConcurrentQueue<Action> pendingUiActions = new();

        // Snapshot state captured during Layout and used for rendering in all event types.
        SessionUpdate[] messagesSnapshot = Array.Empty<SessionUpdate>();
        RequestPermissionRequest pendingPermissionRequestSnapshot;
        AuthMethod[] pendingAuthMethodsSnapshot;
        SessionRequestInputParams pendingInputRequestSnapshot;
        ConnectionStatus connectionStatusSnapshot;
        bool isRunningSnapshot;
        bool isInsideThinkTagSnapshot;
        ModelInfo[] availableModelsSnapshot = Array.Empty<ModelInfo>();
        int selectedModelIndexSnapshot;
        string[] availableModesSnapshot = Array.Empty<string>();
        int selectedModeIndexSnapshot;
        AvailableCommand[] availableCommandsSnapshot = Array.Empty<AvailableCommand>();

        // <think> tag splitting for agents that embed thinking inside normal message chunks.
        const string ThinkTagOpen = "<think>";
        const string ThinkTagClose = "</think>";
        string thinkCarry = "";
        bool isInsideThinkTag;


        ConnectionStatus connectionStatus;
        bool isRunning;
        string sessionId;
        ClientSideConnection conn;
        Process agentProcess;
        StreamReader agentStdoutReader;
        StreamWriter agentStdinWriter;
        StreamReader agentStderrReader;

        CancellationTokenSource connectionCts;
        CancellationTokenSource operationCts;
        bool isConnecting;
        bool isDomainReloading;
        bool shouldInjectHistoryIntoPrompts;

        // Model management
        ModelInfo[] availableModels = Array.Empty<ModelInfo>();
        int selectedModelIndex;

        // Mode management
        string[] availableModes = Array.Empty<string>();
        int selectedModeIndex;

        // Slash commands (ACP available_commands_update)
        AvailableCommand[] availableCommands = Array.Empty<AvailableCommand>();

        // Permission management
        RequestPermissionRequest pendingPermissionRequest;
        TaskCompletionSource<RequestPermissionResponse> pendingPermissionTcs;

        // Auth management
        AuthMethod[] pendingAuthMethods;
        TaskCompletionSource<AuthMethod> pendingAuthTcs;

        // Codely ACP extension: session/request_input
        SessionRequestInputParams pendingInputRequest;
        TaskCompletionSource<JToken> pendingInputTcs;
        string pendingInputText;

        // Input focus management
        const string InputControlName = "AgentInputField";
        bool shouldFocusInput;

        [MenuItem("Tools/Unity ACP Client (Legacy IMGUI)")]
        static void Init()
        {
            var window = (AgentWindow)GetWindow(typeof(AgentWindow));
            window.titleContent = EditorGUIUtility.IconContent("account");
            window.titleContent.text = "AI Agent";

            window.connectionStatus = ConnectionStatus.Pending;
            window.isRunning = false;
            window.sessionId = null;
            window.messages.Clear();
            window.foldoutStates.Clear();
            window.attachedAssets.Clear();
            window.availableModels = Array.Empty<ModelInfo>();
            window.selectedModelIndex = 0;
            window.availableModes = Array.Empty<string>();
            window.selectedModeIndex = 0;
            window.availableCommands = Array.Empty<AvailableCommand>();
            window.pendingPermissionRequest = null;
            window.pendingPermissionTcs = null;
            window.pendingAuthMethods = null;
            window.pendingAuthTcs = null;

            window.Show();
        }

        void Disconnect(bool killAgentProcess = true)
        {
            // This method is invoked by Unity on domain reload (beforeAssemblyReload), editor quit, and when the window closes.
            // It must be best-effort and never leave the window in a "running" state.

            isConnecting = false;
            isRunning = false;
            thinkCarry = string.Empty;
            isInsideThinkTag = false;

            // Drop any queued UI updates from the old domain/session.
            while (pendingUiActions.TryDequeue(out _))
            {
            }

            try
            {
                operationCts?.Cancel();
            }
            catch
            {
                // ignore
            }
            finally
            {
                operationCts?.Dispose();
                operationCts = null;
            }

            try
            {
                connectionCts?.Cancel();
            }
            catch
            {
                // ignore
            }
            finally
            {
                connectionCts?.Dispose();
                connectionCts = null;
            }

            // If we were waiting for UI input (auth/permission), make sure the awaiting tasks are released.
            // Otherwise ConnectAsync/RequestPermissionAsync can hang and leave the window in a stuck state.
            try
            {
                pendingPermissionTcs?.TrySetCanceled();
            }
            catch
            {
                // ignore
            }

            try
            {
                pendingAuthTcs?.TrySetCanceled();
            }
            catch
            {
                // ignore
            }

            pendingPermissionRequest = null;
            pendingPermissionTcs = null;
            pendingAuthMethods = null;
            pendingAuthTcs = null;

            // If the agent is blocked waiting for user input via extMethod, release it gracefully.
            try
            {
                pendingInputTcs?.TrySetResult(new JObject { ["outcome"] = "cancelled" });
            }
            catch
            {
                // ignore
            }

            pendingInputRequest = null;
            pendingInputTcs = null;
            pendingInputText = null;

            sessionId = null;
            availableModels = Array.Empty<ModelInfo>();
            selectedModelIndex = 0;
            availableModes = Array.Empty<string>();
            selectedModeIndex = 0;

            // Cancel/Dispose connection so background reader tasks don't spin during domain reload.
            try
            {
                conn?.Dispose();
            }
            catch
            {
                // ignore
            }
            finally
            {
                conn = null;
            }

            if (agentProcess != null)
            {
                try
                {
                    if (killAgentProcess && !agentProcess.HasExited)
                    {
                        agentProcess.Kill();
                    }
                }
                catch
                {
                    // ignore (process may have already exited or is not killable)
                }
                finally
                {
                    try
                    {
                        agentProcess.Dispose();
                    }
                    catch
                    {
                        // ignore
                    }

                    agentProcess = null;
                    agentStdoutReader = null;
                    agentStdinWriter = null;
                    agentStderrReader = null;
                }

                Logger.LogVerbose(killAgentProcess ? "Disconnected" : "Disconnected (agent kept running)");
            }

            // Reset UI state so we don't get stuck in "Connecting..." forever.
            connectionStatus = ConnectionStatus.Pending;
        }

        void OnInspectorUpdate()
        {
            // Avoid repaint storms during compilation/domain reload (can make Unity appear stuck in Repaint).
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            // Only repaint when we actually have new UI work or the agent is actively running/connecting.
            if (!pendingUiActions.IsEmpty || isRunning || isConnecting)
            {
                Repaint();
            }
        }

        void OnEnable()
        {
            // Avoid duplicate subscriptions across domain reload / window recreation.
            AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;

            EditorApplication.quitting -= HandleEditorQuitting;
            EditorApplication.quitting += HandleEditorQuitting;

            // After domain reload, EditorWindow instances may survive, but all non-serialized fields are reset.
            // Ensure we don't show a stale "Running" state.
            isConnecting = false;
            isRunning = false;
            connectionStatus = ConnectionStatus.Pending;

            pendingInputRequest = null;
            pendingInputTcs = null;
            pendingInputText = null;

            // Restore UI history across domain reloads so the conversation doesn't disappear.
            TryRestoreHistoryFromPersistence();

            // If we kept the agent process alive across domain reload, reattach to its stdio so the ACP session continues.
            TryRestoreLiveTransportFromPersistence();
        }

        void OnDisable()
        {
            // Ensure we don't leave stale subscriptions around when the window is closed/disabled.
            AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
            EditorApplication.quitting -= HandleEditorQuitting;

            // OnDisable is called for both domain reload and window close.
            // During domain reload we already snapshot state in beforeAssemblyReload.
            if (isDomainReloading)
            {
                Disconnect(killAgentProcess: false);
                return;
            }

            // Window closed explicitly: still persist a snapshot so reopening keeps history.
            TryPersistSnapshot();
            Disconnect(killAgentProcess: true);
        }

        void HandleBeforeAssemblyReload()
        {
            isDomainReloading = true;
            TryPersistSnapshot();
            TryPersistLiveTransportForDomainReload();
            Disconnect(killAgentProcess: false);
        }

        void HandleEditorQuitting()
        {
            TryPersistSnapshot();
            Disconnect(killAgentProcess: true);
        }

        void TryPersistSnapshot()
        {
            try
            {
                AgentSessionPersistence.SaveSnapshot(sessionId, messages);
            }
            catch
            {
                // ignore - persistence should never block domain reload
            }
        }

        void TryRestoreHistoryFromPersistence()
        {
            try
            {
                var restored = AgentSessionPersistence.LoadMessages();
                if (restored.Count == 0) return;

                messages.Clear();
                messages.AddRange(restored);

                RebuildDerivedStateFromMessages();
            }
            catch
            {
                // ignore
            }
        }

        void RebuildDerivedStateFromMessages()
        {
            // Restore latest available commands so slash completion works even after domain reload.
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i] is AvailableCommandsUpdateSessionUpdate cmds && cmds.AvailableCommands != null)
                {
                    availableCommands = cmds.AvailableCommands;
                    break;
                }
            }
        }

        const int JsonRpcMethodNotFoundCode = -32601;

        static int GenerateRequestIdSeed()
        {
            // Large positive seed to avoid collisions with any outstanding JSON-RPC responses after domain reload.
            var seed = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0x7fffffff);
            return seed < 1024 ? seed + 1024 : seed;
        }

        void TryPersistLiveTransportForDomainReload()
        {
#if UNITY_EDITOR_WIN
            try
            {
                if (agentProcess == null)
                {
                    Logger.LogVerbose("No agent process; skipping live transport persistence.");
                    return;
                }

                if (agentProcess.HasExited)
                {
                    Logger.LogVerbose("Agent process already exited; skipping live transport persistence.");
                    return;
                }

                if (agentStdoutReader == null || agentStdinWriter == null)
                {
                    Logger.LogVerbose("Missing stdio streams; skipping live transport persistence.");
                    return;
                }

                // Avoid leaking old duplicated handles if a previous reload restore failed.
                if (AgentSessionPersistence.TryGetLiveTransport(out _, out var oldStdin, out var oldStdout, out var oldStderr, out _))
                {
                    Win32HandleUtil.CloseHandleIfValid(oldStdin);
                    Win32HandleUtil.CloseHandleIfValid(oldStdout);
                    Win32HandleUtil.CloseHandleIfValid(oldStderr);
                    AgentSessionPersistence.ClearLiveTransport();
                }

                var stdinStream = agentStdinWriter.BaseStream;
                var stdoutStream = agentStdoutReader.BaseStream;
                var stderrStream = agentStderrReader?.BaseStream;

                if (!Win32HandleUtil.TryDuplicateStreamHandle(stdinStream, out var stdinDup, out var stdinErr))
                {
                    Logger.LogWarning($"Failed to duplicate stdin handle (type={stdinStream?.GetType().FullName ?? "(null)"}, err={stdinErr}).");
                    return;
                }

                if (!Win32HandleUtil.TryDuplicateStreamHandle(stdoutStream, out var stdoutDup, out var stdoutErr))
                {
                    Win32HandleUtil.CloseHandleIfValid(stdinDup);
                    Logger.LogWarning($"Failed to duplicate stdout handle (type={stdoutStream?.GetType().FullName ?? "(null)"}, err={stdoutErr}).");
                    return;
                }

                if (stderrStream == null)
                {
                    Win32HandleUtil.CloseHandleIfValid(stdinDup);
                    Win32HandleUtil.CloseHandleIfValid(stdoutDup);
                    Logger.LogWarning("Failed to duplicate stderr handle (stream is null).");
                    return;
                }

                if (!Win32HandleUtil.TryDuplicateStreamHandle(stderrStream, out var stderrDup, out var stderrErr))
                {
                    Win32HandleUtil.CloseHandleIfValid(stdinDup);
                    Win32HandleUtil.CloseHandleIfValid(stdoutDup);
                    Logger.LogWarning($"Failed to duplicate stderr handle (type={stderrStream.GetType().FullName}, err={stderrErr}).");
                    return;
                }

                var seed = GenerateRequestIdSeed();
                AgentSessionPersistence.SaveLiveTransport(agentProcess.Id, stdinDup, stdoutDup, stderrDup, seed);
                Logger.LogVerbose($"Persisted live transport for domain reload (pid={agentProcess.Id}, seed={seed}).");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to persist live transport: {ex.Message}");
            }
#endif
        }

        void TryRestoreLiveTransportFromPersistence()
        {
#if UNITY_EDITOR_WIN
            try
            {
                if (conn != null) return;

                if (!AgentSessionPersistence.TryGetLiveTransport(out var pid, out var stdinHandle, out var stdoutHandle, out var stderrHandle, out var requestIdSeed))
                {
                    return;
                }

                Process p = null;
                try
                {
                    p = Process.GetProcessById(pid);
                }
                catch
                {
                    Win32HandleUtil.CloseHandleIfValid(stdinHandle);
                    Win32HandleUtil.CloseHandleIfValid(stdoutHandle);
                    Win32HandleUtil.CloseHandleIfValid(stderrHandle);
                    AgentSessionPersistence.ClearLiveTransport();
                    return;
                }

                if (p.HasExited)
                {
                    Win32HandleUtil.CloseHandleIfValid(stdinHandle);
                    Win32HandleUtil.CloseHandleIfValid(stdoutHandle);
                    Win32HandleUtil.CloseHandleIfValid(stderrHandle);
                    AgentSessionPersistence.ClearLiveTransport();
                    try { p.Dispose(); } catch { }
                    return;
                }

                if (!Win32HandleUtil.TryCreateReaderFromHandle(stdoutHandle, out var reader) ||
                    !Win32HandleUtil.TryCreateWriterFromHandle(stdinHandle, out var writer) ||
                    !Win32HandleUtil.TryCreateReaderFromHandle(stderrHandle, out var stderrReader))
                {
                    Win32HandleUtil.CloseHandleIfValid(stdinHandle);
                    Win32HandleUtil.CloseHandleIfValid(stdoutHandle);
                    Win32HandleUtil.CloseHandleIfValid(stderrHandle);
                    AgentSessionPersistence.ClearLiveTransport();
                    try { p.Dispose(); } catch { }
                    return;
                }

                // From here on, the created streams own the duplicated handles.
                AgentSessionPersistence.ClearLiveTransport();

                agentProcess = p;
                agentStdoutReader = reader;
                agentStdinWriter = writer;
                agentStderrReader = stderrReader;

                StartDrainAgentStderr(agentStderrReader);

                conn = new ClientSideConnection(_ => this, agentStdoutReader, agentStdinWriter, requestIdSeed);
                conn.Open();

                sessionId = AgentSessionPersistence.GetSessionId();
                connectionStatus = ConnectionStatus.Success;
                isConnecting = false;
                isRunning = false;
                shouldInjectHistoryIntoPrompts = false;

                Logger.LogVerbose($"Reattached to existing agent process after domain reload (pid={pid}).");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to restore live transport: {ex.Message}");
            }
#endif
        }

        void StartDrainAgentStderr(StreamReader stderr)
        {
            if (stderr == null) return;

            Task.Run(() =>
            {
                try
                {
                    var text = stderr.ReadToEnd();
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Keep agent alive across reload: do not treat stderr as fatal; just surface it.
                        UnityEngine.Debug.LogWarning(text);
                    }
                }
                catch
                {
                    // ignore (process likely exited / pipe closed / domain reload)
                }
            }).Forget();
        }

        async Task ConnectAsync(AgentSettings config, CancellationToken cancellationToken = default)
        {
            Disconnect();

            isConnecting = true;
            connectionStatus = ConnectionStatus.Pending;

            connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ct = connectionCts.Token;

            try
            {
                // Set working directory to Unity project root (parent of Assets folder)
                var projectRoot = Directory.GetParent(Application.dataPath).FullName;

                var startInfo = new ProcessStartInfo
                {
#if UNITY_EDITOR_OSX
                    FileName = "/bin/bash",
                    Arguments = $"-cl '{config.Command} {config.Arguments}'",
#elif UNITY_EDITOR_WIN
                    FileName = "cmd.exe",
                    Arguments = $"/c {config.Command} {config.Arguments}",
#else
                    FileName = config.Command,
                    Arguments = config.Arguments,
#endif
                    WorkingDirectory = projectRoot,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                foreach (var kv in config.EnvironmentVariables)
                {
                    startInfo.EnvironmentVariables[kv.Key] = kv.Value;
                }

                agentProcess = Process.Start(startInfo);

                agentStdoutReader = agentProcess.StandardOutput;
                agentStdinWriter = agentProcess.StandardInput;
                agentStderrReader = agentProcess.StandardError;

                // Consume stderr so the process won't block or crash on EPIPE when the editor reloads.
                StartDrainAgentStderr(agentStderrReader);

                conn = new ClientSideConnection(_ => this, agentStdoutReader, agentStdinWriter, initialRequestId: 0);
                conn.Open();

                Logger.LogVerbose("Connecting...");

                var initRes = await conn.InitializeAsync(new()
                {
                    ProtocolVersion = 1,
                    ClientCapabilities = new()
                    {
                        Fs = new()
                        {
                            ReadTextFile = true,
                            WriteTextFile = true,
                        }
                    },
                    ClientInfo = new()
                    {
                        Name = "UnityClientAgent",
                        Version = "0.1.0",
                    }
                }, ct);

                Logger.LogVerbose($"Connected to agent '{initRes.AgentInfo?.Name}'");

                if (initRes.AuthMethods != null && initRes.AuthMethods.Length > 1)
                {
                    var authTcs = new TaskCompletionSource<AuthMethod>(TaskCreationOptions.RunContinuationsAsynchronously);
                    EnqueueUi(() =>
                    {
                        pendingAuthMethods = initRes.AuthMethods;
                        pendingAuthTcs = authTcs;
                    });

                    var selectedAuthMethod = await authTcs.Task;

                    await conn.AuthenticateAsync(new()
                    {
                        MethodId = selectedAuthMethod.Id,
                    }, ct);

                    Logger.LogVerbose($"Authenticated with method: {selectedAuthMethod.Id}");
                }

                // Try to resume the previous session across domain reloads (ACP supports session/load).
                var persistedSessionId = AgentSessionPersistence.GetSessionId();
                Logger.LogVerbose($"Persisted session id: {(string.IsNullOrWhiteSpace(persistedSessionId) ? "(none)" : persistedSessionId)}");
                string newSessionId = null;
                SessionModelState? modelsState = null;
                SessionModeState? modesState = null;
                var loadedExistingSession = false;

                var sessionLoadUnsupported = AgentSessionPersistence.IsSessionLoadUnsupported();
                Logger.LogVerbose($"Session load unsupported: {sessionLoadUnsupported}");

                if (!sessionLoadUnsupported && !string.IsNullOrWhiteSpace(persistedSessionId))
                {
                    try
                    {
                        var loadRes = await conn.LoadSessionAsync(new LoadSessionRequest
                        {
                            Cwd = projectRoot,
                            SessionId = persistedSessionId,
                        }, ct);

                        newSessionId = persistedSessionId;
                        modelsState = loadRes.Models;
                        modesState = loadRes.Modes;
                        loadedExistingSession = true;
                        Logger.LogVerbose($"Session loaded ({newSessionId}).");
                    }
                    catch (AcpException ex) when (ex.Code == JsonRpcMethodNotFoundCode)
                    {
                        AgentSessionPersistence.MarkSessionLoadUnsupported();
                        Logger.LogWarning($"Agent does not support session/load; falling back to session/new. ({ex.Message})");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"Failed to load session '{persistedSessionId}': {ex.Message}");
                    }
                }

                if (string.IsNullOrWhiteSpace(newSessionId))
                {
                    // Fallback: create a new session.
                    var newSession = await conn.NewSessionAsync(new()
                    {
                        Cwd = projectRoot,
                    }, ct);

                    newSessionId = newSession.SessionId;
                    modelsState = newSession.Models;
                    modesState = newSession.Modes;
                    Logger.LogVerbose($"Session created ({newSessionId}).");
                }

                // Persist for the next domain reload.
                AgentSessionPersistence.SetSessionId(newSessionId);

                ModelInfo[] newAvailableModels = Array.Empty<ModelInfo>();
                var newSelectedModelIndex = 0;
                if (modelsState?.AvailableModels != null && modelsState.AvailableModels.Length > 0)
                {
                    newAvailableModels = modelsState.AvailableModels;
                    newSelectedModelIndex = newAvailableModels
                        .Select((model, index) => (model, index))
                        .FirstOrDefault(x => x.model.ModelId == modelsState.CurrentModelId)
                        .index;
                }

                string[] newAvailableModes = Array.Empty<string>();
                var newSelectedModeIndex = 0;
                if (modesState?.AvailableModes != null && modesState.AvailableModes.Length > 0)
                {
                    newAvailableModes = modesState.AvailableModes.Select(m => m.Id).ToArray();
                    newSelectedModeIndex = newAvailableModes
                        .Select((mode, index) => (mode, index))
                        .FirstOrDefault(x => x.mode == modesState.CurrentModeId)
                        .index;
                }

                EnqueueUi(() =>
                {
                    sessionId = newSessionId;
                    availableModels = newAvailableModels;
                    selectedModelIndex = newSelectedModelIndex;
                    availableModes = newAvailableModes;
                    selectedModeIndex = newSelectedModeIndex;
                    connectionStatus = ConnectionStatus.Success;

                    // If we restored UI history but could not resume an existing agent session, inject history into the
                    // next prompt so the conversation can continue even after domain reload / session recreation.
                    shouldInjectHistoryIntoPrompts = messages.Count > 0 && !loadedExistingSession;
                });
            }
            catch (OperationCanceledException)
            {
                // Expected during domain reload.
                EnqueueUi(() => connectionStatus = ConnectionStatus.Pending);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Connection failed: {ex.Message}");
                EnqueueUi(() => connectionStatus = ConnectionStatus.Failed);
            }
            finally
            {
                EnqueueUi(() => isConnecting = false);
            }
        }

        void OnGUI()
        {
            wordWrapTextAreaStyle ??= new(EditorStyles.textArea)
            {
                wordWrap = true
            };

            // Apply queued UI updates and refresh snapshots only during Layout to keep Layout/Repaint consistent.
            if (Event.current.type == EventType.Layout)
            {
                DrainPendingUiActions();
                CaptureUiSnapshot();
            }

            HandleWindowDragAndDrop();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                using (new EditorGUILayout.VerticalScope(GUILayout.MaxWidth(Math.Min(800, EditorGUIUtility.currentViewWidth - 18))))
                {
                    if (conn == null)
                    {
                        var settings = AgentSettingsConfig.Load();
                        if (settings == null || string.IsNullOrWhiteSpace(settings.Command))
                        {
                            EditorGUILayout.Space();
                            EditorGUILayout.LabelField("No agent has been configured.");
                            if (GUILayout.Button("Open Config File..."))
                            {
                                AgentSettingsConfig.OpenConfigFile();
                            }
                            return;
                        }

                        if (!isConnecting)
                        {
                            ConnectAsync(settings).Forget();
                        }
                    }

                    EditorGUILayout.Space();

                    switch (connectionStatusSnapshot)
                    {
                        case ConnectionStatus.Pending:
                            if (pendingAuthMethodsSnapshot != null)
                            {
                                DrawAuthRequest(pendingAuthMethodsSnapshot);
                            }
                            else
                            {
                                EditorGUILayout.LabelField("Connecting...");
                            }
                            return;
                        case ConnectionStatus.Failed:
                            EditorGUILayout.LabelField("Connection Error");
                            if (GUILayout.Button("Retry"))
                            {
                                var settings = AgentSettingsConfig.Load();
                                ConnectAsync(settings).Forget();
                            }
                            return;

                    }

                    conversationScroll = EditorGUILayout.BeginScrollView(conversationScroll, GUILayout.ExpandHeight(true));

                    try
                    {
                        for (int i = 0; i < messagesSnapshot.Length; i++)
                        {
                            try
                            {
                                DrawMessage(messagesSnapshot[i], i);
                            }
                            catch (Exception ex)
                            {
                                // Prevent IMGUI layout corruption when unexpected render exceptions occur.
                                EditorGUILayout.HelpBox($"Render error: {ex.Message}", MessageType.Error);
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // ignore 'Collection was modified' error
                    }

                    if (pendingPermissionRequestSnapshot != null)
                    {
                        DrawPermissionRequest(pendingPermissionRequestSnapshot);
                    }

                    if (pendingInputRequestSnapshot != null)
                    {
                        DrawInputRequest(pendingInputRequestSnapshot);
                    }

                    EditorGUILayout.EndScrollView();

                    if (isRunningSnapshot)
                    {
                        conversationScroll.y = float.MaxValue;
                    }

                    EditorGUILayout.Space();

                    DrawAttachmentUI();
                    DrawInputField();

                    EditorGUILayout.BeginHorizontal();

                    using (new EditorGUI.DisabledScope(isRunningSnapshot))
                    {
                        // Mode dropdown - only show if modes are available
                        if (availableModesSnapshot != null && availableModesSnapshot.Length > 0)
                        {
                            int newModeIndex = EditorGUILayout.Popup(
                                selectedModeIndexSnapshot,
                                availableModesSnapshot,
                                GUILayout.MinWidth(100));
                            if (newModeIndex != selectedModeIndexSnapshot)
                            {
                                selectedModeIndex = newModeIndex;
                                SetSessionModeAsync(availableModesSnapshot[newModeIndex]).Forget();
                            }
                        }

                        // Model dropdown - only show if models are available
                        if (availableModelsSnapshot != null && availableModelsSnapshot.Length > 0)
                        {
                            int newModelIndex = EditorGUILayout.Popup(
                                selectedModelIndexSnapshot,
                                availableModelsSnapshot.Select(x => x.Name).ToArray(),
                                GUILayout.MinWidth(150));
                            if (newModelIndex != selectedModelIndexSnapshot)
                            {
                                selectedModelIndex = newModelIndex;
                                SetSessionModelAsync(availableModelsSnapshot[newModelIndex].ModelId).Forget();
                            }
                        }
                    }

                    GUILayout.FlexibleSpace();

                    // Only show Stop button when running (no Send button - use Enter to send)
                    if (isRunningSnapshot)
                    {
                        if (GUILayout.Button("Stop", GUILayout.Width(60)))
                        {
                            try
                            {
                                CancelSessionAsync().Forget();
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarning($"Failed to cancel: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        // Show hint text instead of Send button
                        EditorGUILayout.LabelField("Enter 发送 | Shift+Enter 换行", EditorStyles.miniLabel, GUILayout.Width(160));
                    }

                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.Space();
                }

                GUILayout.FlexibleSpace();
            }
        }

        void EnqueueUi(Action action)
        {
            pendingUiActions.Enqueue(action);
        }

        void DrainPendingUiActions()
        {
            while (pendingUiActions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"UI update failed: {ex}");
                }
            }
        }

        void CaptureUiSnapshot()
        {
            messagesSnapshot = messages.ToArray();
            pendingPermissionRequestSnapshot = pendingPermissionRequest;
            pendingAuthMethodsSnapshot = pendingAuthMethods;
            pendingInputRequestSnapshot = pendingInputRequest;
            connectionStatusSnapshot = connectionStatus;
            isRunningSnapshot = isRunning;
            isInsideThinkTagSnapshot = isInsideThinkTag;
            availableModelsSnapshot = availableModels;
            selectedModelIndexSnapshot = selectedModelIndex;
            availableModesSnapshot = availableModes;
            selectedModeIndexSnapshot = selectedModeIndex;
            availableCommandsSnapshot = availableCommands;
        }

        string GetToolCallHeader(ToolCallSessionUpdate update)
        {
            var extracted = TryExtractToolName(update.RawInput);
            var title = string.IsNullOrWhiteSpace(update.Title) ? null : update.Title.Trim();

            var name = title ?? extracted ?? update.Kind.ToString();
            var status = update.Status.ToString();

            if (!string.IsNullOrWhiteSpace(extracted) && !string.Equals(extracted, name, StringComparison.OrdinalIgnoreCase))
            {
                name = $"{name} [{extracted}]";
            }

            return $"{name} · {status}";
        }

        static string TryExtractToolName(JToken rawInput)
        {
            if (rawInput is not JObject obj) return null;

            // Best-effort heuristics: different agents use different key names.
            string[] keys =
            {
                "tool",
                "toolName",
                "tool_name",
                "name",
                "method",
                "action",
                "command",
            };

            foreach (var key in keys)
            {
                if (!obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token)) continue;
                var value = token?.Type == JTokenType.String ? token.Value<string>() : token?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }

            return null;
        }

        void DrawReadOnlyCodeSection(string title, string text)
        {
            text ??= string.Empty;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    EditorGUIUtility.systemCopyBuffer = text;
                }
            }

            var content = new GUIContent(text);
            var width = Mathf.Max(0, EditorGUIUtility.currentViewWidth - 60);
            var height = EditorMarkdownRenderer.CodeBlockStyle.CalcHeight(content, width);
            EditorGUILayout.SelectableLabel(text, EditorMarkdownRenderer.CodeBlockStyle, GUILayout.MinHeight(height + 6));
        }

        void DrawInputField()
        {
            var evt = Event.current;
            
            // Handle Enter to send, Shift+Enter for newline
            if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Return)
            {
                if (!evt.shift && !isRunning && !string.IsNullOrWhiteSpace(inputText))
                {
                    // Enter without shift - send message
                    evt.Use();
                    var userText = inputText;
                    var attachmentsSnapshot = attachedAssets.Where(a => a != null).ToArray();

                    // Clear input immediately for better UX (actual sending uses captured text).
                    inputText = string.Empty;
                    attachedAssets.Clear();
                    GUI.changed = true;
                    Repaint();

                    SendRequestAsync(userText, attachmentsSnapshot).Forget();
                    return;
                }
                // Shift+Enter - let it insert newline naturally
            }
            
            // Auto-focus input field after agent finishes responding
            if (shouldFocusInput && evt.type == EventType.Repaint)
            {
                shouldFocusInput = false;
                EditorGUI.FocusTextInControl(InputControlName);
            }
            
            inputScroll = EditorGUILayout.BeginScrollView(inputScroll, GUILayout.Height(80));
            GUI.SetNextControlName(InputControlName);
            inputText = EditorGUILayout.TextArea(
                inputText,
                wordWrapTextAreaStyle,
                GUILayout.ExpandHeight(true),
                GUILayout.ExpandWidth(true));
            EditorGUILayout.EndScrollView();

            DrawSlashCommandSuggestions();
        }

        void DrawSlashCommandSuggestions()
        {
            if (string.IsNullOrWhiteSpace(inputText)) return;
            if (!inputText.StartsWith("/")) return;

            if (availableCommandsSnapshot == null || availableCommandsSnapshot.Length == 0)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField("Slash commands", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("No commands received from agent yet. Try typing /help.", EditorStyles.miniLabel);
                    if (GUILayout.Button("Insert /help", EditorStyles.miniButton, GUILayout.Width(120)))
                    {
                        inputText = "/help";
                        shouldFocusInput = true;
                    }
                }
                return;
            }

            // Filter by first token after "/"
            var token = inputText.TrimStart().Substring(1);
            var firstToken = token.Split(new[] { ' ', '\t', '\n', '\r' }, 2)[0];
            var query = firstToken ?? string.Empty;

            var matches = availableCommandsSnapshot
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name))
                .Select(c =>
                {
                    var name = c.Name.Trim();
                    var normalized = name.StartsWith("/") ? name : "/" + name;
                    return (cmd: c, name: normalized);
                })
                .Where(x => string.IsNullOrEmpty(query) || x.name.StartsWith("/" + query, StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .ToArray();

            if (matches.Length == 0) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Slash commands", EditorStyles.boldLabel);

                foreach (var m in matches)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(m.name, EditorStyles.miniButtonLeft, GUILayout.Width(160)))
                        {
                            // Insert command into input; if command expects input, add a trailing space.
                            var needsInput = m.cmd.Input != null;
                            inputText = needsInput ? (m.name + " ") : m.name;
                            shouldFocusInput = true;
                        }

                        var desc = string.IsNullOrWhiteSpace(m.cmd.Description) ? "" : m.cmd.Description.Trim();
                        EditorGUILayout.LabelField(desc, EditorStyles.miniLabel);
                    }

                    if (m.cmd.Input != null && !string.IsNullOrWhiteSpace(m.cmd.Input.Hint))
                    {
                        EditorGUILayout.LabelField($"Hint: {m.cmd.Input.Hint}", EditorStyles.miniLabel);
                    }
                }
            }
        }

        void DrawMessage(SessionUpdate update, int index)
        {
            switch (update)
            {
                case UserMessageChunkSessionUpdate userMessage:
                    DrawUserMessageChunk(userMessage);
                    break;
                case AgentMessageChunkSessionUpdate agentMessage:
                    DrawAgentMessageChunk(agentMessage);
                    break;
                case AgentThoughtChunkSessionUpdate agentThought:
                    DrawAgentThoughtChunk(agentThought, index);
                    break;
                case ToolCallSessionUpdate toolCall:
                    DrawToolCall(toolCall);
                    break;
                case ToolCallUpdateSessionUpdate toolCallUpdate:
                    DrawToolCallUpdate(toolCallUpdate);
                    break;
                case PlanSessionUpdate plan:
                    DrawPlan(plan, index);
                    break;
                case AvailableCommandsUpdateSessionUpdate availableCommands:
                    DrawAvailableCommands(availableCommands, index);
                    break;
                case CurrentModeUpdateSessionUpdate currentMode:
                    DrawCurrentMode(currentMode);
                    break;
                default:
                    EditorGUILayout.LabelField($"Unknown update type: {update.GetType().Name}");
                    break;
            }
        }

        void DrawUserMessageChunk(UserMessageChunkSessionUpdate update)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (update.Content is ResourceLinkContentBlock resourceLink)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("📎", GUILayout.Width(14));

                if (resourceLink.Uri.StartsWith("file://"))
                {
                    var path = Path.GetRelativePath(Path.Combine(Application.dataPath, ".."), resourceLink.Uri[7..]);
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (asset == null)
                    {
                        EditorGUILayout.LabelField(resourceLink.Name ?? resourceLink.Uri, EditorStyles.wordWrappedLabel);
                    }
                    else
                    {
                        EditorGUILayout.ObjectField(asset, asset.GetType(), false);
                    }
                }
                else
                {
                    EditorGUILayout.LabelField(resourceLink.Name ?? resourceLink.Uri, EditorStyles.wordWrappedLabel);
                }

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorMarkdownRenderer.Render(GetContentText(update.Content));
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();
        }

        void DrawAgentMessageChunk(AgentMessageChunkSessionUpdate update)
        {
            var text = GetContentText(update.Content);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    EditorGUIUtility.systemCopyBuffer = text ?? "";
                }
            }

            EditorMarkdownRenderer.Render(text);
            EditorGUILayout.Space();
        }

        void DrawAgentThoughtChunk(AgentThoughtChunkSessionUpdate update, int index)
        {
            var key = $"thought:{index}";
            // Default expanded (user expectation: show thinking by default).
            foldoutStates.TryAdd(key, true);

            var isActiveThinking = isInsideThinkTagSnapshot && index == messagesSnapshot.Length - 1;
            var label = isActiveThinking ? "Thinking..." : "Thoughts";

            // Header row: foldout + copy button (prevents the button from overlapping the rendered text).
            using (new EditorGUILayout.HorizontalScope())
            {
                var foldout = EditorGUILayout.Foldout(foldoutStates[key], label, true);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    EditorGUIUtility.systemCopyBuffer = GetContentText(update.Content) ?? "";
                }
                foldoutStates[key] = foldout;
            }

            if (foldoutStates[key])
            {
                EditorGUI.indentLevel++;
                EditorMarkdownRenderer.Render(GetContentText(update.Content));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
        }

        void DrawToolCall(ToolCallSessionUpdate update)
        {
            var key = $"tool:{update.ToolCallId}";
            // Default expanded so name/input/output are always visible without extra clicks.
            foldoutStates.TryAdd(key, true);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var foldout = EditorGUILayout.Foldout(foldoutStates[key], GetToolCallHeader(update), true);
            if (foldout)
            {
                EditorGUILayout.Space();

                EditorGUILayout.LabelField($"{update.Kind} · {update.Status} · {update.ToolCallId}", EditorStyles.miniLabel);
                EditorGUILayout.Space(2);

                DrawReadOnlyCodeSection("Input", update.RawInput?.ToString() ?? "");

                EditorGUILayout.Space();

                if (update.RawOutput != null)
                {
                    DrawReadOnlyCodeSection("Output", update.RawOutput?.ToString() ?? "");
                }

                if (update.Content != null)
                {
                    EditorGUILayout.LabelField("Content", EditorStyles.boldLabel);
                    foreach (var content in update.Content)
                    {
                        EditorMarkdownRenderer.Render(content switch
                        {
                            ContentToolCallContent c => GetContentText(c.Content),
                            DiffToolCallContent diff => diff.Path,
                            _ => content.ToString(),
                        });
                    }
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();

            foldoutStates[key] = foldout;
        }

        void DrawToolCallUpdate(ToolCallUpdateSessionUpdate _)
        {
            // Do nothing...
        }

        void DrawPlan(PlanSessionUpdate update, int index)
        {
            var key = $"plan:{index}";
            foldoutStates.TryAdd(key, false);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var foldout = EditorGUILayout.Foldout(foldoutStates[key], "Plan", true);
            if (foldout)
            {
                EditorGUILayout.Space();

                foreach (var entry in update.Entries)
                {
                    EditorGUILayout.LabelField($"- {entry}", EditorStyles.wordWrappedLabel);
                }
            }
            EditorGUILayout.Space();

            foldoutStates[key] = foldout;

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        void DrawAvailableCommands(AvailableCommandsUpdateSessionUpdate update, int index)
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                var key = $"available_commands:{index}";
                foldoutStates.TryAdd(key, true);

                var foldout = EditorGUILayout.Foldout(foldoutStates[key], "Available Commands", true);
                if (foldout)
                {
                    EditorGUILayout.Space();

                    foreach (var cmd in update.AvailableCommands)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var name = cmd.Name?.Trim() ?? "";
                            var normalized = name.StartsWith("/") ? name : "/" + name;
                            if (GUILayout.Button(normalized, EditorStyles.miniButtonLeft, GUILayout.Width(180)))
                            {
                                inputText = cmd.Input != null ? (normalized + " ") : normalized;
                                shouldFocusInput = true;
                            }

                            EditorGUILayout.LabelField(cmd.Description ?? "", EditorStyles.miniLabel);
                        }
                    }
                }

                foldoutStates[key] = foldout;
            }

            EditorGUILayout.Space();
        }

        void DrawPermissionRequest(RequestPermissionRequest requestSnapshot)
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Permission Request", EditorStyles.boldLabel);

            var toolCall = requestSnapshot.ToolCall.ToObject<ToolCallSessionUpdate>(AcpJson.Serializer);
            EditorGUILayout.TextArea(toolCall.Title, EditorMarkdownRenderer.CodeBlockStyle);

            EditorGUILayout.BeginHorizontal();
            foreach (var option in requestSnapshot.Options)
            {
                var buttonLabel = option.Kind switch
                {
                    PermissionOptionKind.AllowOnce => "Allow",
                    PermissionOptionKind.AllowAlways => "Allow Always",
                    PermissionOptionKind.RejectOnce => "Reject",
                    PermissionOptionKind.RejectAlways => "Reject Always",
                    _ => default,
                };

                if (GUILayout.Button(buttonLabel))
                {
                    HandlePermissionResponse(option.OptionId);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        void DrawInputRequest(SessionRequestInputParams requestSnapshot)
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var title = string.IsNullOrEmpty(requestSnapshot.Title) ? "Input Required" : requestSnapshot.Title;
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

            if (!string.IsNullOrEmpty(requestSnapshot.Prompt))
            {
                EditorMarkdownRenderer.Render(requestSnapshot.Prompt);
                EditorGUILayout.Space(4);
            }

            if (!string.IsNullOrEmpty(requestSnapshot.Context))
            {
                EditorGUILayout.LabelField(requestSnapshot.Context, EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space(4);
            }

            var ui = requestSnapshot.Ui ?? "input";
            if (ui == "textarea")
            {
                pendingInputText = EditorGUILayout.TextArea(pendingInputText ?? "", GUILayout.MinHeight(80));
            }
            else
            {
                pendingInputText = EditorGUILayout.TextField(pendingInputText ?? "");
            }

            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Submit", GUILayout.Width(80)))
                {
                    CompleteInputRequest(new JObject
                    {
                        ["outcome"] = "submitted",
                        ["text"] = pendingInputText ?? ""
                    });
                }

                if (GUILayout.Button("Cancel", GUILayout.Width(80)))
                {
                    CompleteInputRequest(new JObject { ["outcome"] = "cancelled" });
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        void CompleteInputRequest(JToken result)
        {
            pendingInputTcs?.TrySetResult(result);
            pendingInputRequest = null;
            pendingInputTcs = null;
            pendingInputText = null;
            Repaint();
        }

        void HandlePermissionResponse(string optionId)
        {
            pendingPermissionTcs.TrySetResult(new RequestPermissionResponse
            {
                Outcome = new SelectedRequestPermissionOutcome
                {
                    OptionId = optionId,
                }
            });

            pendingPermissionRequest = null;
            pendingPermissionTcs = null;

            Repaint();
        }

        void DrawAuthRequest(AuthMethod[] authMethodsSnapshot)
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Authentication Required", EditorStyles.boldLabel);

            foreach (var authMethod in authMethodsSnapshot)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                EditorGUILayout.LabelField($"{authMethod.Name}", EditorStyles.boldLabel);

                if (!string.IsNullOrEmpty(authMethod.Description))
                {
                    EditorGUILayout.LabelField($"{authMethod.Description}", EditorStyles.wordWrappedLabel);
                }

                if (GUILayout.Button("Select", GUILayout.Width(80)))
                {
                    HandleAuthResponse(authMethod);
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        void HandleAuthResponse(AuthMethod authMethod)
        {
            pendingAuthTcs.TrySetResult(authMethod);

            pendingAuthMethods = null;
            pendingAuthTcs = null;

            Repaint();
        }

        void DrawCurrentMode(CurrentModeUpdateSessionUpdate update)
        {
            EditorGUILayout.BeginHorizontal(GUI.skin.box);
            EditorGUILayout.LabelField($"Current Mode: {update.CurrentModeId}", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
        }

        void HandleWindowDragAndDrop()
        {
            var evt = Event.current;

            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();

                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        if (obj != null && !attachedAssets.Contains(obj))
                        {
                            attachedAssets.Add(obj);
                        }
                    }

                    Repaint();
                }

                evt.Use();
            }
        }

        void DrawAttachmentUI()
        {
            if (attachedAssets.Count == 0) return;

            EditorGUILayout.Space();

            for (int i = 0; i < attachedAssets.Count; i++)
            {
                var asset = attachedAssets[i];

                using (new EditorGUILayout.HorizontalScope())
                {
                    attachedAssets[i] = EditorGUILayout.ObjectField(asset, typeof(UnityEngine.Object), allowSceneObjects: true);

                    if (GUILayout.Button($"×", EditorStyles.miniButton, GUILayout.Width(30)))
                    {
                        attachedAssets.RemoveAt(i);
                    }
                }
            }
        }

        string GetContentText(ContentBlock content)
        {
            if (content == null) return "";

            var result = content switch
            {
                TextContentBlock text => text.Text,
                ResourceLinkContentBlock resourceLink => $"[{resourceLink.Name ?? "Resource"}]({resourceLink.Uri})",
                _ => content.ToString()
            };

            return result;
        }

        async Task SendRequestAsync(string userText, UnityEngine.Object[] attachmentsSnapshot, CancellationToken cancellationToken = default)
        {
            if (conn == null || string.IsNullOrEmpty(sessionId)) return;
            if (string.IsNullOrWhiteSpace(userText)) return;

            operationCts?.Cancel();
            operationCts?.Dispose();
            operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            isRunning = true;
            try
            {
                var historyContext = shouldInjectHistoryIntoPrompts ? BuildHistoryContextForPrompt() : null;
                attachmentsSnapshot ??= Array.Empty<UnityEngine.Object>();

                // Add user message
                messages.Add(new UserMessageChunkSessionUpdate
                {
                    Content = new TextContentBlock
                    {
                        Text = userText,
                    }
                });

                // Build prompt content blocks
                var promptBlocks = new List<ContentBlock>
                {
                    historyContext != null ? new TextContentBlock { Text = historyContext } : null,
                    new TextContentBlock { Text = userText }
                };
                promptBlocks.RemoveAll(b => b == null);

                // Add attached assets as resource links
                foreach (var asset in attachmentsSnapshot)
                {
                    if (asset == null) continue;

                    var assetPath = AssetDatabase.GetAssetPath(asset);
                    string uri;

                    if (string.IsNullOrEmpty(assetPath))
                    {
                        var gameObject = asset as GameObject;
                        if (gameObject != null && gameObject.scene.IsValid())
                        {
                            var scenePath = gameObject.scene.path;
                            var instanceId = gameObject.GetInstanceID();
                            uri = new Uri($"file://{Path.GetFullPath(scenePath)}?instanceID={instanceId}").AbsoluteUri;
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else
                    {
                        var fullPath = Path.GetFullPath(assetPath);
                        uri = new Uri(fullPath).AbsoluteUri;
                    }

                    var resourceLink = new ResourceLinkContentBlock
                    {
                        Name = asset.name,
                        Uri = uri
                    };

                    promptBlocks.Add(resourceLink);

                    // Add resource link to messages for UI display
                    messages.Add(new UserMessageChunkSessionUpdate
                    {
                        Content = resourceLink
                    });
                }

                var request = new PromptRequest
                {
                    SessionId = sessionId,
                    Prompt = promptBlocks.ToArray(),
                };

                try
                {
                    await conn.PromptAsync(request, operationCts.Token);
                }
                catch (IOException ioEx)
                {
                    // Win32 error 232 (ERROR_NO_DATA) / broken pipe can happen if the agent process exited or a pipe got closed
                    // during/after domain reload. Recover by disconnecting so OnGUI can reconnect.
                    Logger.LogWarning($"Prompt failed due to I/O error: {ioEx.Message}");
                    EnqueueUi(() => connectionStatus = ConnectionStatus.Failed);
                    Disconnect(killAgentProcess: true);
                    return;
                }
                catch (ObjectDisposedException odEx)
                {
                    Logger.LogWarning($"Prompt failed (disposed): {odEx.Message}");
                    EnqueueUi(() => connectionStatus = ConnectionStatus.Failed);
                    Disconnect(killAgentProcess: true);
                    return;
                }

                // Once we've injected history successfully, stop doing it for subsequent prompts in this session.
                shouldInjectHistoryIntoPrompts = false;
            }
            finally
            {
                EnqueueUi(() =>
                {
                    isRunning = false;
                    shouldFocusInput = true;
                });
            }
        }

        string BuildHistoryContextForPrompt()
        {
            const int maxEntries = 30;
            const int maxChars = 12000;

            var entries = new List<string>(maxEntries);
            var totalChars = 0;

            for (int i = messages.Count - 1; i >= 0 && entries.Count < maxEntries; i--)
            {
                string entry = messages[i] switch
                {
                    UserMessageChunkSessionUpdate u => FormatHistoryEntry("User", GetContentText(u.Content)),
                    AgentMessageChunkSessionUpdate a => FormatHistoryEntry("Assistant", GetContentText(a.Content)),
                    ToolCallSessionUpdate t => FormatToolHistoryEntry(t),
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(entry)) continue;

                var addLen = entry.Length + 2;
                if (totalChars + addLen > maxChars) break;

                entries.Add(entry);
                totalChars += addLen;
            }

            if (entries.Count == 0) return null;
            entries.Reverse();

            return
                "Previous conversation context (for continuity; do not repeat it verbatim):\n\n" +
                string.Join("\n\n", entries) +
                "\n\n---\n\nCurrent user request:";
        }

        static string FormatHistoryEntry(string role, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return $"{role}: {text.Trim()}";
        }

        string FormatToolHistoryEntry(ToolCallSessionUpdate update)
        {
            if (update == null) return null;

            var header = GetToolCallHeader(update);
            var status = update.Status.ToString();
            var sb = new StringBuilder();
            sb.Append($"Tool: {header} · {status}");

            var output = update.RawOutput?.ToString();
            if (!string.IsNullOrWhiteSpace(output))
            {
                sb.Append("\nToolOutput: ");
                sb.Append(Truncate(output.Trim(), 1200));
            }

            return sb.ToString();
        }

        static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen) return text;
            return text.Substring(0, maxLen) + "\n…(truncated)…";
        }

        async Task SetSessionModelAsync(string modelId, CancellationToken cancellationToken = default)
        {
            if (conn == null || string.IsNullOrEmpty(sessionId)) return;

            operationCts?.Cancel();
            operationCts?.Dispose();
            operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            isRunning = true;
            try
            {
                await conn.SetSessionModelAsync(new()
                {
                    SessionId = sessionId,
                    ModelId = modelId,
                }, operationCts.Token);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to change model: {ex.Message}");
            }
            finally
            {
                EnqueueUi(() => isRunning = false);
            }
        }

        async Task SetSessionModeAsync(string modeId, CancellationToken cancellationToken = default)
        {
            if (conn == null || string.IsNullOrEmpty(sessionId)) return;

            operationCts?.Cancel();
            operationCts?.Dispose();
            operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            isRunning = true;
            try
            {
                await conn.SetSessionModeAsync(new()
                {
                    SessionId = sessionId,
                    ModeId = modeId,
                }, operationCts.Token);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to change mode: {ex.Message}");
            }
            finally
            {
                EnqueueUi(() => isRunning = false);
            }
        }

        async Task CancelSessionAsync(CancellationToken cancellationToken = default)
        {
            if (!isRunning) return;

            try
            {
                operationCts?.Cancel();

                if (conn != null && !string.IsNullOrEmpty(sessionId))
                {
                    await conn.CancelAsync(new()
                    {
                        SessionId = sessionId,
                    }, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to cancel session: {ex.Message}");
            }
            finally
            {
                EnqueueUi(() =>
                {
                    isRunning = false;
                    shouldFocusInput = true;
                });
            }
        }

        public async ValueTask<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken = default)
        {
            // Marshal UI state changes to main thread (during Layout) to avoid IMGUI layout errors.
            var tcs = new TaskCompletionSource<RequestPermissionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            EnqueueUi(() =>
            {
                pendingPermissionRequest = request;
                pendingPermissionTcs = tcs;
            });

            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task;
        }

        void ApplySessionUpdate(SessionUpdate update)
        {
            if (update == null) return;

            switch (update)
            {
                case AgentMessageChunkSessionUpdate agentMessageChunk:
                    if (agentMessageChunk.Content is TextContentBlock textBlock)
                    {
                        ProcessAgentTextChunk(textBlock.Text ?? string.Empty);
                    }
                    else
                    {
                        AppendAgentMessageText(GetContentText(agentMessageChunk.Content));
                    }
                    break;
                case AgentThoughtChunkSessionUpdate agentThoughtChunk:
                    AppendAgentThinkingText(GetContentText(agentThoughtChunk.Content));
                    break;
                case ToolCallUpdateSessionUpdate toolCallUpdate:
                    ApplyToolCallUpdate(toolCallUpdate);
                    break;
                case AvailableCommandsUpdateSessionUpdate cmds:
                    availableCommands = cmds.AvailableCommands ?? Array.Empty<AvailableCommand>();
                    messages.Add(cmds); // keep visible in transcript as well
                    break;
                default:
                    messages.Add(update);
                    break;
            }
        }

        void ApplyToolCallUpdate(ToolCallUpdateSessionUpdate update)
        {
            if (update == null) return;

            // Find the most recent matching tool call by id (updates can arrive out-of-order).
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i] is ToolCallSessionUpdate existing && existing.ToolCallId == update.ToolCallId)
                {
                    var combinedContent = new List<ToolCallContent>(existing.Content ?? Array.Empty<ToolCallContent>());
                    if (update.Content != null) combinedContent.AddRange(update.Content);

                    messages[i] = existing with
                    {
                        Status = update.Status ?? existing.Status,
                        Title = update.Title ?? existing.Title,
                        Content = combinedContent.ToArray(),
                        Kind = update.Kind ?? existing.Kind,
                        Locations = update.Locations ?? existing.Locations,
                        RawInput = update.RawInput ?? existing.RawInput,
                        RawOutput = update.RawOutput ?? existing.RawOutput,
                    };
                    return;
                }
            }

            // If we couldn't find the original tool call, materialize a best-effort entry so the UI isn't confusing.
            messages.Add(new ToolCallSessionUpdate
            {
                ToolCallId = update.ToolCallId,
                Title = update.Title ?? "(tool)",
                Content = update.Content ?? Array.Empty<ToolCallContent>(),
                Kind = update.Kind ?? ToolKind.Other,
                Locations = update.Locations ?? Array.Empty<ToolCallLocation>(),
                RawInput = update.RawInput,
                RawOutput = update.RawOutput,
                Status = update.Status ?? ToolCallStatus.Pending,
            });
        }

        void ProcessAgentTextChunk(string chunk)
        {
            if (string.IsNullOrEmpty(chunk) && string.IsNullOrEmpty(thinkCarry)) return;

            var s = thinkCarry + chunk;
            thinkCarry = string.Empty;

            int idx = 0;
            while (idx < s.Length)
            {
                if (!isInsideThinkTag)
                {
                    var start = s.IndexOf(ThinkTagOpen, idx, StringComparison.OrdinalIgnoreCase);
                    if (start < 0)
                    {
                        AppendWithPartialTagHandling(s.Substring(idx), ThinkTagOpen, isThink: false);
                        return;
                    }

                    AppendAgentMessageText(s.Substring(idx, start - idx));
                    idx = start + ThinkTagOpen.Length;
                    isInsideThinkTag = true;
                }
                else
                {
                    var end = s.IndexOf(ThinkTagClose, idx, StringComparison.OrdinalIgnoreCase);
                    if (end < 0)
                    {
                        AppendWithPartialTagHandling(s.Substring(idx), ThinkTagClose, isThink: true);
                        return;
                    }

                    AppendAgentThinkingText(s.Substring(idx, end - idx));
                    idx = end + ThinkTagClose.Length;
                    isInsideThinkTag = false;
                }
            }
        }

        void AppendWithPartialTagHandling(string remainder, string tag, bool isThink)
        {
            if (string.IsNullOrEmpty(remainder)) return;

            var keep = PartialTagSuffixLength(remainder, tag);
            var emitLen = remainder.Length - keep;

            if (emitLen > 0)
            {
                var emit = remainder.Substring(0, emitLen);
                if (isThink) AppendAgentThinkingText(emit);
                else AppendAgentMessageText(emit);
            }

            thinkCarry = keep > 0 ? remainder.Substring(emitLen) : string.Empty;
        }

        static int PartialTagSuffixLength(string text, string tag)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(tag)) return 0;

            // We only care about *partial* tags, not full matches (those would have been found by IndexOf).
            var max = Math.Min(text.Length, tag.Length - 1);
            for (int len = max; len > 0; len--)
            {
                if (text.EndsWith(tag.Substring(0, len), StringComparison.OrdinalIgnoreCase))
                {
                    return len;
                }
            }

            return 0;
        }

        void AppendAgentMessageText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (messages.Count > 0 && messages[^1] is AgentMessageChunkSessionUpdate lastAgentMessage)
            {
                var lastText = GetContentText(lastAgentMessage.Content);
                messages[^1] = new AgentMessageChunkSessionUpdate
                {
                    Content = new TextContentBlock { Text = lastText + text }
                };
                return;
            }

            messages.Add(new AgentMessageChunkSessionUpdate
            {
                Content = new TextContentBlock { Text = text }
            });
        }

        void AppendAgentThinkingText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (messages.Count > 0 && messages[^1] is AgentThoughtChunkSessionUpdate lastAgentThought)
            {
                var lastText = GetContentText(lastAgentThought.Content);
                messages[^1] = new AgentThoughtChunkSessionUpdate
                {
                    Content = new TextContentBlock { Text = lastText + text }
                };
                return;
            }

            messages.Add(new AgentThoughtChunkSessionUpdate
            {
                Content = new TextContentBlock { Text = text }
            });
        }

        public ValueTask SessionNotificationAsync(SessionNotification notification, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var update = notification.Update;
            EnqueueUi(() => ApplySessionUpdate(update));
            return default;
        }

        public async ValueTask<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken = default)
        {
            var directory = Path.GetDirectoryName(request.Path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(request.Path, request.Content);

            return new WriteTextFileResponse();
        }

        public async ValueTask<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken = default)
        {
            using var stream = new FileStream(
                request.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true
            );

            using var reader = new StreamReader(
                stream,
                encoding: System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096
            );

            string content;

            if (request.Line.HasValue || request.Limit.HasValue)
            {
                content = await ReadLinesAsync(reader, request.Line ?? 1, request.Limit, cancellationToken);
            }
            else
            {
                content = await reader.ReadToEndAsync();
            }

            return new ReadTextFileResponse
            {
                Content = content
            };
        }

        async Task<string> ReadLinesAsync(StreamReader reader, uint startLine, uint? limit, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            uint currentLine = 1;
            uint linesRead = 0;

            // Skip lines
            while (currentLine < startLine && !reader.EndOfStream)
            {
                await reader.ReadLineAsync();
                currentLine++;
                cancellationToken.ThrowIfCancellationRequested();
            }

            while (!reader.EndOfStream)
            {
                if (limit.HasValue && linesRead >= limit.Value)
                {
                    break;
                }

                var line = await reader.ReadLineAsync();
                if (line == null) break;

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }
                sb.Append(line);

                linesRead++;
                cancellationToken.ThrowIfCancellationRequested();
            }

            return sb.ToString();
        }

        public ValueTask<CreateTerminalResponse> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask<TerminalOutputRequest> TerminalOutputAsync(TerminalOutputRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask<ReleaseTerminalResponse> ReleaseTerminalAsync(ReleaseTerminalRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask<WaitForTerminalExitResponse> WaitForTerminalExitAsync(WaitForTerminalExitRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask<KillTerminalCommandResponse> KillTerminalCommandAsync(KillTerminalCommandRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async ValueTask<JToken> ExtMethodAsync(string method, JToken request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (method == "session/request_input")
            {
                // Parse params and block until user submits/cancels.
                var p = request?.ToObject<SessionRequestInputParams>(AcpJson.Serializer) ?? new SessionRequestInputParams();

                var tcs = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                EnqueueUi(() =>
                {
                    // Replace any existing pending request.
                    pendingInputTcs?.TrySetResult(new JObject { ["outcome"] = "cancelled" });

                    pendingInputRequest = p;
                    pendingInputText = p.DefaultValue;
                    pendingInputTcs = tcs;
                });

                using var reg = cancellationToken.Register(() =>
                {
                    tcs.TrySetResult(new JObject { ["outcome"] = "cancelled" });
                });

                var result = await tcs.Task;

                // Clear UI state if no newer request replaced this one.
                EnqueueUi(() =>
                {
                    if (pendingInputTcs == tcs)
                    {
                        pendingInputRequest = null;
                        pendingInputTcs = null;
                        pendingInputText = null;
                    }
                });

                return result;
            }

            // Unknown extension method
            throw new NotImplementedException();
        }

        public ValueTask ExtNotificationAsync(string method, JToken notification, CancellationToken cancellationToken = default)
        {
            // Ignore unknown notifications.
            return default;
        }

        [Serializable]
        sealed class SessionRequestInputParams
        {
            public string SessionId { get; set; }
            public string ToolCallId { get; set; }
            public string Ui { get; set; }
            public string Title { get; set; }
            public string Prompt { get; set; }
            public string Context { get; set; }
            public string Placeholder { get; set; }
            public string DefaultValue { get; set; }
            public JToken Meta { get; set; }
        }
    }
}