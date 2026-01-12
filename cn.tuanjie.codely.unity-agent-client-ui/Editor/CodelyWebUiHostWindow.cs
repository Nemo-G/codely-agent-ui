using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Codely.UnityAgentClientUI
{
    /// <summary>
    /// Windows-only: starts `codely serve web-ui` and embeds an external UI window (prefer Tauri exe; fallback to Edge/Chrome app mode)
    /// pointing to http://127.0.0.1:3939 inside a Unity EditorWindow.
    /// </summary>
    public sealed class CodelyWebUiHostWindow : EditorWindow
    {
        // codely serve web-ui defaults to 3939 (unless --port is specified)
        const int DefaultPort = 3939;
        const string DefaultUrl = "http://127.0.0.1:3939";
        const float ToolbarHeight = 24f;
        const string TauriExeEnv1 = "UNITY_AGENT_CLIENT_UI_EXE";
        const string TauriExeEnv2 = "CODELY_UNITY_AGENT_CLIENT_UI_EXE";

        const string SessionKey_ServerPid = "Codely.UnityAgentClientUI.ServerPid";
        const string SessionKey_UiPid = "Codely.UnityAgentClientUI.UiPid";
        const string SessionKey_UiIsTauri = "Codely.UnityAgentClientUI.UiIsTauri";
        const string SessionKey_BrowserUserDataDir = "Codely.UnityAgentClientUI.BrowserUserDataDir";

        Process serveProcess;
        Process uiProcess;
        IntPtr uiHwnd;
        IntPtr hostHwnd;
        Rect embedRectScreenPoints;

        string lastError;
        string statusLine;
        double nextFindWindowAt;
        double nextServerCheckAt;
        bool isEmbedded;
        int lastX, lastY, lastW, lastH;
        string edgeUserDataDir;
        bool uiIsTauri;
        bool isDomainReloading;

        [MenuItem("Tools/Unity ACP Client")]
        static void OpenMenu() => OpenWindow();

        public static CodelyWebUiHostWindow OpenWindow()
        {
            var w = GetWindow<CodelyWebUiHostWindow>();
            w.titleContent = new GUIContent("AI Agent");
            w.Show();
            return w;
        }

        void OnEnable()
        {
            EditorApplication.update += Tick;

            // Avoid duplicate subscriptions across domain reload / window recreation.
            AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;

            EditorApplication.quitting -= HandleEditorQuitting;
            EditorApplication.quitting += HandleEditorQuitting;

            // If the window survived a domain reload, restore live external processes so the UI doesn't refresh.
            TryRestoreLiveProcessesFromDomainReload();
            StartIfNeeded();
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;

            AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
            EditorApplication.quitting -= HandleEditorQuitting;

            // During domain reload, do NOT kill external processes. We keep them alive and reattach after reload.
            if (isDomainReloading)
            {
                // Reset local handles so the old domain doesn't keep manipulating window handles.
                uiHwnd = IntPtr.Zero;
                hostHwnd = IntPtr.Zero;
                isEmbedded = false;
                return;
            }

            ClearPersistedDomainReloadState();
            StopAll(forceKill: true);
        }

        void HandleBeforeAssemblyReload()
        {
            isDomainReloading = true;
            PersistLiveProcessesForDomainReload();
        }

        void HandleEditorQuitting()
        {
            isDomainReloading = false;
            ClearPersistedDomainReloadState();
            StopAll(forceKill: true);
        }

        void PersistLiveProcessesForDomainReload()
        {
            try
            {
                if (IsProcessAlive(serveProcess))
                {
                    SessionState.SetInt(SessionKey_ServerPid, serveProcess.Id);
                }
                else
                {
                    SessionState.EraseInt(SessionKey_ServerPid);
                }

                if (IsProcessAlive(uiProcess))
                {
                    SessionState.SetInt(SessionKey_UiPid, uiProcess.Id);
                }
                else
                {
                    SessionState.EraseInt(SessionKey_UiPid);
                }

                SessionState.SetBool(SessionKey_UiIsTauri, uiIsTauri);
                SessionState.SetString(SessionKey_BrowserUserDataDir, edgeUserDataDir ?? string.Empty);
            }
            catch
            {
                // ignore - domain reload should never be blocked by persistence failures
            }
        }

        void TryRestoreLiveProcessesFromDomainReload()
        {
            // Best effort: restore any saved PIDs from the previous domain so we don't restart processes.
            try
            {
                var serverPid = SessionState.GetInt(SessionKey_ServerPid, 0);
                if (serverPid > 0)
                {
                    try
                    {
                        var p = Process.GetProcessById(serverPid);
                        if (!p.HasExited) serveProcess = p;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                var uiPidSaved = SessionState.GetInt(SessionKey_UiPid, 0);
                if (uiPidSaved > 0)
                {
                    try
                    {
                        var p = Process.GetProcessById(uiPidSaved);
                        if (!p.HasExited) uiProcess = p;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                uiIsTauri = SessionState.GetBool(SessionKey_UiIsTauri, false);

                var ud = SessionState.GetString(SessionKey_BrowserUserDataDir, string.Empty);
                edgeUserDataDir = string.IsNullOrWhiteSpace(ud) ? null : ud;

                // Force re-discovery + re-embed with the new domain (HWNDs are not stable across reload).
                uiHwnd = IntPtr.Zero;
                hostHwnd = IntPtr.Zero;
                isEmbedded = false;
                nextFindWindowAt = 0;
                nextServerCheckAt = 0;
            }
            catch
            {
                // ignore
            }
            finally
            {
                // Clear after restore so future manual window opens don't try to attach to stale PIDs.
                ClearPersistedDomainReloadState();
            }
        }

        static void ClearPersistedDomainReloadState()
        {
            try
            {
                SessionState.EraseInt(SessionKey_ServerPid);
                SessionState.EraseInt(SessionKey_UiPid);
                SessionState.EraseBool(SessionKey_UiIsTauri);
                SessionState.EraseString(SessionKey_BrowserUserDataDir);
            }
            catch
            {
                // ignore
            }
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar, GUILayout.Height(ToolbarHeight)))
            {
                if (GUILayout.Button("Restart", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    ClearPersistedDomainReloadState();
                    StopAll(forceKill: true);
                    StartIfNeeded(forceRestart: true);
                }

                if (GUILayout.Button("Force Restart", EditorStyles.toolbarButton, GUILayout.Width(95)))
                {
                    ForceRestartServer();
                }

                if (GUILayout.Button("Stop", EditorStyles.toolbarButton, GUILayout.Width(50)))
                {
                    ClearPersistedDomainReloadState();
                    StopAll(forceKill: true);
                }

                if (GUILayout.Button("Open Browser", EditorStyles.toolbarButton, GUILayout.Width(90)))
                {
                    Application.OpenURL(DefaultUrl);
                }

                if (GUILayout.Button("Log CWD", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    UnityEngine.Debug.Log($"[Codely WebUI] server cwd={GuessWorkspaceRoot()}");
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

#if !UNITY_EDITOR_WIN
            EditorGUILayout.HelpBox("Embedded Web UI is only supported on Windows Editor.", MessageType.Info);
            if (GUILayout.Button("Open " + DefaultUrl))
            {
                Application.OpenURL(DefaultUrl);
            }
            return;
#endif

            // Reserve the rest of the window for the embedded UI.
            var embedRect = GUILayoutUtility.GetRect(
                GUIContent.none,
                GUIStyle.none,
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(true));

            // Convert embed rect (GUI coords) to screen points for Win32 embedding.
            var tl = GUIUtility.GUIToScreenPoint(new Vector2(embedRect.xMin, embedRect.yMin));
            var br = GUIUtility.GUIToScreenPoint(new Vector2(embedRect.xMax, embedRect.yMax));
            embedRectScreenPoints = Rect.MinMaxRect(tl.x, tl.y, br.x, br.y);

            if (!isEmbedded)
            {
                var msg =
                    serveProcess == null ? "Starting web-ui server..." :
                    uiProcess == null ? "Starting web-ui window..." :
                    "Waiting for web-ui window...";
                GUI.Box(embedRect, msg);
            }
        }

        void Tick()
        {
            try
            {
                var isServerReachable = IsPortOpen("127.0.0.1", DefaultPort, timeoutMs: 50);

                // Update status.
                var serverLabel =
                    IsProcessAlive(serveProcess) ? "Running" :
                    isServerReachable ? "External" :
                    "Stopped";

                var uiLabel =
                    IsProcessAlive(uiProcess) ? (uiIsTauri ? "Tauri" : "Browser") :
                    "Stopped";

                statusLine = $"URL: {DefaultUrl} · Server: {serverLabel} · UI: {uiLabel}";

                // Keep server alive.
                if (!IsProcessAlive(serveProcess))
                {
                    if (serveProcess != null)
                    {
                        // Server exited unexpectedly.
                        serveProcess = null;
                    }

                    // If something else already serves the port, don't start a competing instance.
                    if (!isServerReachable)
                    {
                        StartServer();
                    }
                }

                // Wait for server to be reachable before launching the UI window.
                if (uiProcess == null)
                {
                    if (EditorApplication.timeSinceStartup >= nextServerCheckAt)
                    {
                        nextServerCheckAt = EditorApplication.timeSinceStartup + 0.5;
                        if (isServerReachable)
                        {
                            StartUiWindow();
                        }
                    }

                    return;
                }

                if (!IsProcessAlive(uiProcess))
                {
                    StopUiIfNeeded(forceKill: false);
                    Repaint();
                    return;
                }

                // Resolve host (Editor) HWND from the embed rect each tick.
                // Docked windows can change internal HWNDs across layout/refresh.
                if (embedRectScreenPoints.width > 1f && embedRectScreenPoints.height > 1f)
                {
                    if (Win32WindowEmbedding.TryGetUnityGuiViewHwndFromScreenPoint(embedRectScreenPoints.center, out var host))
                    {
                        hostHwnd = host;
                    }
                }

                // Fallback: resolve via reflection if sampling failed.
                if (hostHwnd == IntPtr.Zero)
                {
                    if (!Win32WindowEmbedding.TryGetEditorWindowHwnd(this, out hostHwnd, out var err))
                    {
                        lastError = $"Failed to get EditorWindow HWND: {err}";
                        return;
                    }
                }

                // Find UI window handle (poll a bit; browser may take time to create a window).
                if (uiHwnd == IntPtr.Zero)
                {
                    if (EditorApplication.timeSinceStartup < nextFindWindowAt) return;
                    nextFindWindowAt = EditorApplication.timeSinceStartup + 0.2;

                    uiProcess.Refresh();
                    Win32WindowEmbedding.TryFindTopLevelWindowForProcess(uiProcess.Id, out uiHwnd);
                }

                if (uiHwnd != IntPtr.Zero && !Win32WindowEmbedding.IsValidWindowHandle(uiHwnd))
                {
                    // If the window got destroyed/recreated (or we captured the wrong handle), retry discovery.
                    uiHwnd = IntPtr.Zero;
                    isEmbedded = false;
                    return;
                }

                if (uiHwnd == IntPtr.Zero) return;
                if (embedRectScreenPoints.width <= 1 || embedRectScreenPoints.height <= 1) return;

                Win32WindowEmbedding.EnsureEmbedded(uiHwnd, hostHwnd);

                if (Win32WindowEmbedding.TryGetClientRectForScreenRect(hostHwnd, embedRectScreenPoints, out var x, out var y, out var w, out var h))
                {
                    if (x != lastX || y != lastY || w != lastW || h != lastH)
                    {
                        lastX = x; lastY = y; lastW = w; lastH = h;
                        Win32WindowEmbedding.SetChildBounds(uiHwnd, x, y, w, h, repaint: true);
                    }
                }

                isEmbedded = true;
                lastError = null;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        void StartIfNeeded(bool forceRestart = false)
        {
            if (forceRestart)
            {
                StopAll(forceKill: true);
            }

            lastError = null;
            isEmbedded = false;
            uiHwnd = IntPtr.Zero;
            hostHwnd = IntPtr.Zero;
            nextFindWindowAt = 0;
            nextServerCheckAt = 0;
            lastX = lastY = lastW = lastH = int.MinValue;

            if (!IsProcessAlive(serveProcess))
            {
                // If something else already serves the port, don't start a competing instance.
                if (!IsPortOpen("127.0.0.1", DefaultPort, timeoutMs: 50))
                {
                    StartServer();
                }
            }
        }

        void StartServer()
        {
            try
            {
                if (IsProcessAlive(serveProcess)) return;

                var wd = GuessWorkspaceRoot();
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c codely serve web-ui --port {DefaultPort}",
                    WorkingDirectory = wd,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                UnityEngine.Debug.Log($"[Codely WebUI] start server: cwd={wd}");
                serveProcess = Process.Start(startInfo);
                if (serveProcess == null)
                {
                    lastError = "Failed to start `codely serve web-ui`";
                }
            }
            catch (Exception ex)
            {
                lastError = $"Failed to start server: {ex.Message}";
            }
        }

        void ForceRestartServer()
        {
            try
            {
                ClearPersistedDomainReloadState();
                StopAll(forceKill: true);

                // If 3939 is already occupied by an external server, Unity's auto-start logic will NOT replace it.
                // Here we explicitly kill the listener(s) and restart with the Unity project root as cwd.
                var pids = FindListeningPids(DefaultPort);
                if (pids.Length > 0)
                {
                    foreach (var pid in pids)
                    {
                        if (pid == Process.GetCurrentProcess().Id) continue;
                        KillProcessTree(pid);
                    }
                    UnityEngine.Debug.Log($"[Codely WebUI] force restart: killed port {DefaultPort} listeners: {string.Join(",", pids)}");
                }

                StartIfNeeded(forceRestart: true);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[Codely WebUI] force restart failed: {ex.Message}");
            }
        }

        void StartUiWindow()
        {
            try
            {
                if (IsProcessAlive(uiProcess)) return;

                // Prefer launching a Tauri UI exe if present (so we can embed a real Tauri window).
                if (TryFindTauriUiExecutable(out var tauriExe, out _))
                {
                    uiIsTauri = true;
                    var tauriStartInfo = new ProcessStartInfo
                    {
                        FileName = tauriExe,
                        WorkingDirectory = Path.GetDirectoryName(tauriExe) ?? ".",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    // Best-effort: allow the Tauri app to discover the target URL (if it supports it).
                    tauriStartInfo.EnvironmentVariables["UNITY_AGENT_CLIENT_UI_URL"] = DefaultUrl;

                    uiProcess = Process.Start(tauriStartInfo);
                    if (uiProcess == null)
                    {
                        lastError = "Failed to start Tauri UI window";
                    }

                    return;
                }

                uiIsTauri = false;

                if (!TryFindBrowserExecutable(out var edgePath, out var err))
                {
                    lastError = err;
                    return;
                }

                edgeUserDataDir = MakeTempUserDataDir();
                // `--start-fullscreen` makes it behave like pressing F11 (hide browser UI/chrome).
                var args = $"--app={DefaultUrl} --new-window --no-first-run --start-fullscreen --user-data-dir=\"{edgeUserDataDir}\"";

                var browserStartInfo = new ProcessStartInfo
                {
                    FileName = edgePath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                uiProcess = Process.Start(browserStartInfo);
                if (uiProcess == null)
                {
                    lastError = "Failed to start browser window";
                }
            }
            catch (Exception ex)
            {
                lastError = $"Failed to start web-ui window: {ex.Message}";
            }
        }

        void StopAll(bool forceKill)
        {
            StopUiIfNeeded(forceKill);
            StopServerIfNeeded(forceKill);
        }

        void StopUiIfNeeded(bool forceKill)
        {
            try
            {
                if (uiProcess == null) return;
                if (forceKill && !uiProcess.HasExited)
                {
                    KillProcessTree(uiProcess.Id);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { uiProcess?.Dispose(); } catch { }
                uiProcess = null;
                uiHwnd = IntPtr.Zero;
                hostHwnd = IntPtr.Zero;
                isEmbedded = false;
                uiIsTauri = false;

                TryDeleteDirectory(edgeUserDataDir);
                edgeUserDataDir = null;
            }
        }

        void StopServerIfNeeded(bool forceKill)
        {
            try
            {
                if (serveProcess == null) return;
                if (forceKill && !serveProcess.HasExited)
                {
                    KillProcessTree(serveProcess.Id);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { serveProcess?.Dispose(); } catch { }
                serveProcess = null;
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
                using var client = new System.Net.Sockets.TcpClient();
                var t = client.ConnectAsync(host, port);
                return t.Wait(timeoutMs) && client.Connected;
            }
            catch
            {
                return false;
            }
        }

        static string GuessWorkspaceRoot()
        {
            // IMPORTANT: always treat the currently opened Unity project as the workspace root.
            // `codely serve web-ui` should resolve relative paths/config from the Unity project root,
            // not from this package's repo folder (or any parent that happens to contain a .git/CODELY.md).
            return Directory.GetParent(Application.dataPath)?.FullName ?? ".";
        }

        static bool TryFindTauriUiExecutable(out string path, out string error)
        {
            path = null;
            error = null;

            try
            {
                // Prefer explicit configuration via env var.
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

                // Dev convenience: if this repo layout exists, pick a release build under tauri-ui.
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                var repoRoot = Directory.GetParent(projectRoot)?.FullName;
                if (!string.IsNullOrWhiteSpace(repoRoot))
                {
                    var releaseDir = Path.Combine(repoRoot, "tauri-ui", "src-tauri", "target", "release");
                    string[] exeNames =
                    {
                        "UnityAgentClientUI.exe",
                        "unity-agent-client-ui.exe",
                        "tauri-ui.exe",
                    };

                    foreach (var name in exeNames)
                    {
                        var candidate = Path.Combine(releaseDir, name);
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

            return false;
        }

        static bool TryFindBrowserExecutable(out string path, out string error)
        {
            path = null;
            error = null;

            // Allow override via env var for enterprise images.
            var overridePath = Environment.GetEnvironmentVariable("CODELY_WEB_UI_BROWSER");
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                path = overridePath;
                return true;
            }

            // Common install locations.
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            };

            foreach (var c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c) && File.Exists(c))
                {
                    path = c;
                    return true;
                }
            }

            // Try PATH resolution (`where msedge` / `where chrome`).
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where msedge || where chrome",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };

                using var p = Process.Start(psi);
                var output = p?.StandardOutput?.ReadToEnd()?.Trim();
                p?.WaitForExit(800);

                if (!string.IsNullOrWhiteSpace(output))
                {
                    var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    if (File.Exists(firstLine))
                    {
                        path = firstLine;
                        return true;
                    }
                }
            }
            catch
            {
                // ignore
            }

            error = "No supported browser found (msedge/chrome). Install Microsoft Edge or Google Chrome, or set env CODELY_WEB_UI_BROWSER to the full path of your browser executable.";
            return false;
        }

        static string MakeTempUserDataDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "CodelyUnityWebUi", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // ignore
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
                };

                using var p = Process.Start(psi);
                p?.WaitForExit(2000);
            }
            catch
            {
                // ignore
            }
        }

        static int[] FindListeningPids(int port)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c netstat -ano -p tcp | findstr LISTENING | findstr :{port}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using var p = Process.Start(psi);
                var output = p?.StandardOutput?.ReadToEnd() ?? "";
                p?.WaitForExit(800);

                // Example line:
                //   TCP    127.0.0.1:3939         0.0.0.0:0              LISTENING       12345
                var matches = Regex.Matches(output, @"LISTENING\s+(\d+)\s*$", RegexOptions.Multiline);
                if (matches.Count == 0) return Array.Empty<int>();

                var list = new System.Collections.Generic.List<int>(matches.Count);
                foreach (Match m in matches)
                {
                    if (m.Groups.Count < 2) continue;
                    if (int.TryParse(m.Groups[1].Value, out var pid) && pid > 0 && !list.Contains(pid))
                    {
                        list.Add(pid);
                    }
                }
                return list.ToArray();
            }
            catch
            {
                return Array.Empty<int>();
            }
        }
    }
}


