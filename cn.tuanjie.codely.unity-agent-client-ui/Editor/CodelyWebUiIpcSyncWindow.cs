using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Codely.UnityAgentClientUI
{
    /// <summary>
    /// IPC Sync mode:
    /// - Starts `codely serve web-ui`
    /// - Launches Tauri UI executable
    /// - Publishes this EditorWindow's screen-rect via shared memory for the Tauri app to follow
    ///
    /// This window is intentionally isolated from Legacy Browser (SetParent) implementation.
    /// </summary>
    public sealed class CodelyWebUiIpcSyncWindow : EditorWindow
    {
        const int DefaultPort = 3999;
        const string DefaultUrl = "http://localhost:3999";

        const string TauriExeEnv1 = "UNITY_AGENT_CLIENT_UI_EXE";
        const string TauriExeEnv2 = "CODELY_UNITY_AGENT_CLIENT_UI_EXE";
        const string TauriIpcPathEnv = "UNITY_AGENT_CLIENT_UI_IPC_PATH";
        const string TauriIpcNameEnv = "UNITY_AGENT_CLIENT_UI_IPC_NAME";
        const string TauriIpcModeEnv = "UNITY_AGENT_CLIENT_UI_IPC_MODE";
        const string TauriIpcDebugEnv = "UNITY_AGENT_CLIENT_UI_IPC_DEBUG";

        const string SessionKey_ServerPid = "Codely.UnityAgentClientUI.IpcSync.ServerPid";
        const string SessionKey_UiPid = "Codely.UnityAgentClientUI.IpcSync.UiPid";
        const string SessionKey_IpcPath = "Codely.UnityAgentClientUI.IpcSync.IpcPath";

        const float ToolbarHeight = 22f;

        Process serveProcess;
        Process uiProcess;
        string ipcPath;
        CodelyWindowSyncSharedMemory syncIpc;

        Rect embedRectScreenPoints;
        double lastOnGuiAt;
        bool wroteRectOnce;
        string lastError;
        string statusLine;
        double nextDebugLogAt;
        bool debugLogs;
        bool hasLastGoodRect;
        int lastGoodX, lastGoodY, lastGoodW, lastGoodH;
        long lastOwnerHwnd;
        double inactiveSince;

#if UNITY_EDITOR_WIN
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        static int GetForegroundPid()
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(h, out var pid);
            return (int)pid;
        }
#endif

        [MenuItem("Tools/Unity ACP Client (IPC Sync)")]
        static void OpenMenu() => OpenWindow();

        static CodelyWebUiIpcSyncWindow OpenWindow()
        {
            var w = GetWindow<CodelyWebUiIpcSyncWindow>();
            w.titleContent = new GUIContent("AI Agent (IPC)");
            w.Show();
            return w;
        }

        void OnEnable()
        {
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += PersistForDomainReload;
            EditorApplication.quitting += HandleQuit;

            TryRestoreFromDomainReload();
            StartIfNeeded();
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= PersistForDomainReload;
            EditorApplication.quitting -= HandleQuit;

            // In IPC mode we keep behavior simple: close kills processes.
            StopAll(forceKill: true);
        }

        void HandleQuit()
        {
            StopAll(forceKill: true);
        }

        void PersistForDomainReload()
        {
            try
            {
                if (IsProcessAlive(serveProcess)) SessionState.SetInt(SessionKey_ServerPid, serveProcess.Id);
                if (IsProcessAlive(uiProcess)) SessionState.SetInt(SessionKey_UiPid, uiProcess.Id);
                if (!string.IsNullOrWhiteSpace(ipcPath)) SessionState.SetString(SessionKey_IpcPath, ipcPath);
            }
            catch
            {
                // ignore
            }
        }

        void TryRestoreFromDomainReload()
        {
            try
            {
                var serverPid = SessionState.GetInt(SessionKey_ServerPid, 0);
                var uiPid = SessionState.GetInt(SessionKey_UiPid, 0);
                var savedIpcPath = SessionState.GetString(SessionKey_IpcPath, null);

                if (serverPid > 0)
                {
                    TryAttachProcess(serverPid, out serveProcess);
                }

                if (uiPid > 0)
                {
                    TryAttachProcess(uiPid, out uiProcess);
                }

                if (!string.IsNullOrWhiteSpace(savedIpcPath))
                {
                    ipcPath = savedIpcPath;
                }
            }
            catch
            {
                // ignore
            }
        }

        void OnGUI()
        {
            lastOnGuiAt = EditorApplication.timeSinceStartup;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar, GUILayout.Height(ToolbarHeight)))
            {
                if (GUILayout.Button("Restart", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    StopAll(forceKill: true);
                    StartIfNeeded(forceRestart: true);
                }

                if (GUILayout.Button("Stop", EditorStyles.toolbarButton, GUILayout.Width(50)))
                {
                    StopAll(forceKill: true);
                }

                if (GUILayout.Button("Open Browser", EditorStyles.toolbarButton, GUILayout.Width(90)))
                {
                    Application.OpenURL(DefaultUrl);
                }

                debugLogs = GUILayout.Toggle(debugLogs, "Debug", EditorStyles.toolbarButton, GUILayout.Width(60));
                if (GUILayout.Button("Dump IPC", EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    DumpIpcSnapshot();
                }

                GUILayout.FlexibleSpace();

                if (!string.IsNullOrWhiteSpace(statusLine))
                {
                    GUILayout.Label(statusLine, EditorStyles.miniLabel);
                }
            }

            if (!string.IsNullOrWhiteSpace(lastError))
            {
                EditorGUILayout.HelpBox(lastError, MessageType.Error);
            }

            // Reserve the rest of the window for the UI rect we publish.
            var embedRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            var tl = GUIUtility.GUIToScreenPoint(new Vector2(embedRect.xMin, embedRect.yMin));
            var br = GUIUtility.GUIToScreenPoint(new Vector2(embedRect.xMax, embedRect.yMax));
            embedRectScreenPoints = Rect.MinMaxRect(tl.x, tl.y, br.x, br.y);

            if (!wroteRectOnce)
            {
                GUI.Box(embedRect, "IPC Sync: waiting for first rect publish...");
            }
        }

        void Tick()
        {
            // Keep OnGUI running so GUIToScreenPoint + embed rect stay fresh (otherwise we never publish).
            Repaint();

            var serverLabel = IsProcessAlive(serveProcess) ? "Running" : (IsPortOpen("127.0.0.1", DefaultPort, 50) ? "External" : "Stopped");
            var uiLabel = IsProcessAlive(uiProcess) ? "Tauri" : "Stopped";

            try
            {
                // Ensure IPC exists early so we always launch Tauri with a valid IPC_PATH/NAME.
                EnsureSyncIpc();

                // Keep server alive (only start if port isn't already served).
                if (!IsProcessAlive(serveProcess) && !IsPortOpen("127.0.0.1", DefaultPort, 50))
                {
                    StartServer();
                }

                // Launch Tauri if not running.
                if (!IsProcessAlive(uiProcess))
                {
                    StartTauri();
                }

                // Most aggressive policy (per user request): never hide during focus/resize transitions.
                // We only hide when the window is explicitly stopped/closed.
                var rectOk = embedRectScreenPoints.width > 1f && embedRectScreenPoints.height > 1f;

                // Determine owner HWND so the Tauri window stays above this EditorWindow (no SetParent).
                // Note: we do NOT hide when the Tauri window itself takes focus; focus is allowed.
                long ownerHwnd = 0;
#if UNITY_EDITOR_WIN
                if (Win32WindowEmbedding.TryGetEditorWindowHwnd(this, out var owner, out _))
                {
                    ownerHwnd = owner.ToInt64();
                }
#endif
                lastOwnerHwnd = ownerHwnd;

                // If rect becomes temporarily invalid (e.g. 1x1 during dock/resize), keep using lastGoodRect.
                // This avoids flicker + avoids accidentally hiding the Tauri window during resize drags.
                if (!rectOk && hasLastGoodRect)
                {
                    syncIpc.WriteRect(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, ownerHwnd: ownerHwnd);
                    wroteRectOnce = true;
                    lastError = null;

                    if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                    {
                        nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                        UnityEngine.Debug.Log($"[IPC Sync] rect transient; using lastGood rect: seq={syncIpc.LastSeq} rect=({lastGoodX},{lastGoodY},{lastGoodW},{lastGoodH}) owner=0x{ownerHwnd:X} ipc={ipcPath}");
                    }
                    return;
                }

                if (!rectOk && !hasLastGoodRect)
                {
                    // No valid rect yet; do not hide.
                    wroteRectOnce = false;
                    if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                    {
                        nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                        UnityEngine.Debug.Log($"[IPC Sync] rect not ready yet: rect=({embedRectScreenPoints.xMin:0.##},{embedRectScreenPoints.yMin:0.##},{embedRectScreenPoints.width:0.##},{embedRectScreenPoints.height:0.##}) ppp={EditorGUIUtility.pixelsPerPoint:0.##} ipc={ipcPath} seq={(syncIpc != null ? syncIpc.LastSeq : 0)}");
                    }
                    return;
                }

                var ppp = EditorGUIUtility.pixelsPerPoint;
                var xPx = Mathf.RoundToInt(embedRectScreenPoints.xMin * ppp);
                var yPx = Mathf.RoundToInt(embedRectScreenPoints.yMin * ppp);
                var wPx = Mathf.RoundToInt(embedRectScreenPoints.width * ppp);
                var hPx = Mathf.RoundToInt(embedRectScreenPoints.height * ppp);

                hasLastGoodRect = true;
                lastGoodX = xPx; lastGoodY = yPx; lastGoodW = wPx; lastGoodH = hPx;

                syncIpc.WriteRect(xPx, yPx, wPx, hPx, visible: true, ownerHwnd: ownerHwnd);
                wroteRectOnce = true;
                lastError = null;

                if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                {
                    nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                    UnityEngine.Debug.Log($"[IPC Sync] write rect: seq={syncIpc.LastSeq} x={xPx} y={yPx} w={wPx} h={hPx} owner=0x{ownerHwnd:X} ipc={ipcPath} pid={(IsProcessAlive(uiProcess) ? uiProcess.Id : 0)}");
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                if (debugLogs)
                {
                    UnityEngine.Debug.LogError($"[IPC Sync] exception: {ex}");
                }
            }
            finally
            {
                statusLine = $"IPC Sync · URL: {DefaultUrl} · Server: {serverLabel} · UI: {uiLabel}";
                if (IsProcessAlive(uiProcess))
                {
                    statusLine += $" · pid:{uiProcess.Id}";
                }
                if (!string.IsNullOrWhiteSpace(ipcPath))
                {
                    statusLine += $" · ipc:{Path.GetFileName(ipcPath)}";
                }
                if (syncIpc != null)
                {
                    statusLine += $" · seq:{syncIpc.LastSeq}";
                }
                if (lastOwnerHwnd != 0)
                {
                    statusLine += $" · owner:0x{lastOwnerHwnd:X}";
                }
            }
        }

        void StartIfNeeded(bool forceRestart = false)
        {
            if (forceRestart)
            {
                StopAll(forceKill: true);
            }

            lastError = null;
            wroteRectOnce = false;

            // Ensure IPC exists early (so we can pass env vars to Tauri).
            EnsureSyncIpc();

            if (!IsProcessAlive(serveProcess) && !IsPortOpen("127.0.0.1", DefaultPort, 50))
            {
                StartServer();
            }

            if (!IsProcessAlive(uiProcess))
            {
                StartTauri();
            }
        }

        void EnsureSyncIpc()
        {
            if (syncIpc != null) return;
            if (string.IsNullOrWhiteSpace(ipcPath))
            {
                ipcPath = CodelyWindowSyncSharedMemory.DefaultMappingPath();
            }
            syncIpc = CodelyWindowSyncSharedMemory.OpenOrCreate(ipcPath);
            if (debugLogs)
            {
                UnityEngine.Debug.Log($"[IPC Sync] opened mapping: {ipcPath}");
            }
        }

        void StartServer()
        {
            try
            {
                if (IsProcessAlive(serveProcess)) return;

                var wd = GuessWorkspaceRoot();
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c codely serve web-ui",
                    WorkingDirectory = wd,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                serveProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                lastError = $"Failed to start server: {ex.Message}";
            }
        }

        void StartTauri()
        {
            try
            {
                if (IsProcessAlive(uiProcess)) return;
                EnsureSyncIpc();

                if (!TryFindTauriUiExecutable(out var tauriExe, out var err))
                {
                    lastError = err ?? "Tauri executable not found";
                    return;
                }

                if (!TauriBinaryLooksIpcCapable(tauriExe))
                {
                    lastError =
                        "This tauri binary does NOT include IPC sync support (it is missing IPC env-var strings). " +
                        "Please rebuild `tauri-ui` from the latest source and copy it into `Editor/Bin/win/tauri-ui.exe`.";
                    if (debugLogs)
                    {
                        UnityEngine.Debug.LogError($"[IPC Sync] tauri binary missing IPC strings: {tauriExe}");
                    }
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = tauriExe,
                    WorkingDirectory = Path.GetDirectoryName(tauriExe) ?? ".",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.EnvironmentVariables["UNITY_AGENT_CLIENT_UI_URL"] = DefaultUrl;
                psi.EnvironmentVariables[TauriIpcPathEnv] = ipcPath;
                psi.EnvironmentVariables[TauriIpcNameEnv] = ipcPath; // back-compat
                psi.EnvironmentVariables[TauriIpcModeEnv] = "sync";
                psi.EnvironmentVariables[TauriIpcDebugEnv] = debugLogs ? "1" : "0";

                uiProcess = Process.Start(psi);
                if (debugLogs)
                {
                    UnityEngine.Debug.Log($"[IPC Sync] start tauri: exe={tauriExe} pid={(uiProcess != null ? uiProcess.Id : 0)} ipc={ipcPath}");
                }
            }
            catch (Exception ex)
            {
                lastError = $"Failed to start Tauri: {ex.Message}";
            }
        }

        void StopAll(bool forceKill)
        {
            try { syncIpc?.WriteVisible(false); } catch { }
            try { syncIpc?.Dispose(); } catch { }
            syncIpc = null;

            StopProcess(ref uiProcess, forceKill);
            StopProcess(ref serveProcess, forceKill);
        }

        static void StopProcess(ref Process p, bool forceKill)
        {
            try
            {
                if (p == null) return;
                if (forceKill && !p.HasExited)
                {
                    KillProcessTree(p.Id);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { p?.Dispose(); } catch { }
                p = null;
            }
        }

        static bool TryAttachProcess(int pid, out Process p)
        {
            p = null;
            try
            {
                var proc = Process.GetProcessById(pid);
                if (proc.HasExited) return false;
                p = proc;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool IsProcessAlive(Process p)
        {
            try { return p != null && !p.HasExited; }
            catch { return false; }
        }

        static bool IsPortOpen(string host, int port, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                var ar = client.BeginConnect(host, port, null, null);
                var ok = ar.AsyncWaitHandle.WaitOne(timeoutMs);
                if (!ok) return false;
                client.EndConnect(ar);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static string GuessWorkspaceRoot()
        {
            // Best-effort: try project root.
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            return string.IsNullOrWhiteSpace(projectRoot) ? "." : projectRoot;
        }

        static bool TryFindTauriUiExecutable(out string path, out string error)
        {
            path = null;
            error = null;

            try
            {
                var env1 = Environment.GetEnvironmentVariable(TauriExeEnv1);
                if (!string.IsNullOrWhiteSpace(env1) && File.Exists(env1))
                {
                    path = env1;
                    return true;
                }

                var env2 = Environment.GetEnvironmentVariable(TauriExeEnv2);
                if (!string.IsNullOrWhiteSpace(env2) && File.Exists(env2))
                {
                    path = env2;
                    return true;
                }

                var pkgRoot = TryGetThisPackageRootDir();
                if (!string.IsNullOrWhiteSpace(pkgRoot))
                {
                    string platformDir =
#if UNITY_EDITOR_WIN
                        "win";
#elif UNITY_EDITOR_OSX
                        "mac";
#else
                        "linux";
#endif

#if UNITY_EDITOR_WIN
                    string[] names = { "tauri-ui.exe", "UnityAgentClientUI.exe", "unity-agent-client-ui.exe" };
#else
                    string[] names = { "tauri-ui", "UnityAgentClientUI", "unity-agent-client-ui" };
#endif

                    foreach (var n in names)
                    {
                        var candidate = Path.Combine(pkgRoot, "Editor", "Bin", platformDir, n);
                        if (File.Exists(candidate))
                        {
                            path = candidate;
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            error = "IPC Sync requires a built Tauri UI executable under `cn.tuanjie.codely.unity-agent-client-ui/Editor/Bin/<platform>/tauri-ui(.exe)` or env UNITY_AGENT_CLIENT_UI_EXE.";
            return false;
        }

        static string TryGetThisPackageRootDir()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CodelyWebUiIpcSyncWindow).Assembly);
                var resolved = info?.resolvedPath;
                return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
            }
            catch
            {
                return null;
            }
        }

        static void KillProcessTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {pid} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using var p = Process.Start(psi);
                p?.WaitForExit(2000);
            }
            catch
            {
                // ignore
            }
        }

        void DumpIpcSnapshot()
        {
            try
            {
                EnsureSyncIpc();
                if (string.IsNullOrWhiteSpace(ipcPath) || !File.Exists(ipcPath))
                {
                    UnityEngine.Debug.LogWarning("[IPC Sync] No ipcPath file to dump.");
                    return;
                }

                var bytes = new byte[64];
                using (var fs = File.Open(ipcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Read(bytes, 0, bytes.Length);
                }

                uint magic = BitConverter.ToUInt32(bytes, 0);
                uint ver = BitConverter.ToUInt32(bytes, 4);
                uint seq = BitConverter.ToUInt32(bytes, 8);
                int x = BitConverter.ToInt32(bytes, 12);
                int y = BitConverter.ToInt32(bytes, 16);
                int w = BitConverter.ToInt32(bytes, 20);
                int h = BitConverter.ToInt32(bytes, 24);
                uint flags = BitConverter.ToUInt32(bytes, 32);
                long owner = BitConverter.ToInt64(bytes, 40);

                UnityEngine.Debug.Log($"[IPC Sync] dump path={ipcPath} magic=0x{magic:X8} ver={ver} seq={seq} rect=({x},{y},{w},{h}) flags=0x{flags:X8} owner={owner}");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[IPC Sync] dump failed: " + ex.Message);
            }
        }

        static bool TauriBinaryLooksIpcCapable(string exePath)
        {
            // Quick heuristic: the IPC build should embed these env-var strings.
            // This prevents silently running an older binary that can never sync.
            try
            {
                return FileContainsAscii(exePath, "UNITY_AGENT_CLIENT_UI_IPC_MODE") &&
                       FileContainsAscii(exePath, "UNITY_AGENT_CLIENT_UI_IPC_PATH");
            }
            catch
            {
                return false;
            }
        }

        static bool FileContainsAscii(string filePath, string needle)
        {
            var n = Encoding.ASCII.GetBytes(needle);
            if (n.Length == 0) return true;

            using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Max(64 * 1024, n.Length * 4)];
            var carryLen = 0;

            while (true)
            {
                var read = fs.Read(buf, carryLen, buf.Length - carryLen);
                if (read <= 0) break;
                var total = carryLen + read;

                if (IndexOf(buf, total, n) >= 0) return true;

                carryLen = Math.Min(n.Length - 1, total);
                Buffer.BlockCopy(buf, total - carryLen, buf, 0, carryLen);
            }

            return false;
        }

        static int IndexOf(byte[] haystack, int hayLen, byte[] needle)
        {
            if (needle.Length == 0) return 0;
            if (hayLen < needle.Length) return -1;

            for (var i = 0; i <= hayLen - needle.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) return i;
            }

            return -1;
        }
    }
}


