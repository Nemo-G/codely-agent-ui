using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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
        // codely serve web-ui defaults to 3939 (unless --port is specified)
        const int DefaultPort = 3939;
        const string DefaultUrl = "http://127.0.0.1:3939";

        const string TauriExeEnv1 = "UNITY_AGENT_CLIENT_UI_EXE";
        const string TauriExeEnv2 = "CODELY_UNITY_AGENT_CLIENT_UI_EXE";
        const string TauriIpcPathEnv = "UNITY_AGENT_CLIENT_UI_IPC_PATH";
        const string TauriIpcNameEnv = "UNITY_AGENT_CLIENT_UI_IPC_NAME";
        const string TauriIpcModeEnv = "UNITY_AGENT_CLIENT_UI_IPC_MODE";
        const string TauriIpcDebugEnv = "UNITY_AGENT_CLIENT_UI_IPC_DEBUG";
        const string ServerCwdEnv = "UNITY_AGENT_CLIENT_UI_SERVER_CWD";
        const string CodelyExeEnv = "CODELY_EXE";
        const string CodelyExeEnv2 = "UNITY_AGENT_CLIENT_UI_CODELY_EXE";

        const string SessionKey_ServerPid = "Codely.UnityAgentClientUI.IpcSync.ServerPid";
        const string SessionKey_UiPid = "Codely.UnityAgentClientUI.IpcSync.UiPid";
        const string SessionKey_IpcPath = "Codely.UnityAgentClientUI.IpcSync.IpcPath";
        const string SessionKey_UserHidden = "Codely.UnityAgentClientUI.IpcSync.UserHidden";
        const string SessionKey_Detached = "Codely.UnityAgentClientUI.IpcSync.Detached";

        const float ToolbarHeight = 22f;

        Process serveProcess;
        Process uiProcess;
        string ipcPath;
        CodelyWindowSyncSharedMemory syncIpc;
        readonly object serverLogLock = new object();
        string serverStdoutTail;
        string serverStderrTail;

        readonly object uiLogLock = new object();
        string uiStdoutTail;
        string uiStderrTail;

        Rect embedRectScreenPoints;
        double lastOnGuiAt;
        bool wroteRectOnce;
        string lastError;
        string statusLine;
        double nextDebugLogAt;
        double nextServerProbeAt;
        bool lastServerPortOpen;
        double nextServerStartAt;
        double nextUiStartAt;
        bool debugLogs;
        double nextForegroundProbeAt;
        int lastForegroundPid;
        int lastTauriPid;
        bool hasLastGoodRect;
        int lastGoodX, lastGoodY, lastGoodW, lastGoodH;
        long lastOwnerHwnd;
        uint lastDropSeq;

        // macOS: IPC sync (rect publish + repaint) can be expensive. Throttle to reduce editor UI overhead.
        double nextRepaintAt;
        double nextPublishAt;
        bool hasLastPublishedRect;
        int lastPublishedX, lastPublishedY, lastPublishedW, lastPublishedH;
        bool lastPublishedVisible;
        bool lastPublishedActive;
        long lastPublishedOwnerHwnd;

        bool userHidden;
        bool detached;
        double forceShowUntil;
        double lastFocusedAt;
        double lastUnityAppActiveAt;
        bool lastDesiredVisible;
        bool lastDesiredActive;

        // Focus-driven auto hide/show (requested):
        // - When this window loses focus to another Unity editor window/tab -> hide.
        // - When this window regains focus -> show once.
        // IMPORTANT: Do not hide if the user clicked into the tauri-ui window itself.
        bool pendingAutoHide;
        double pendingAutoHideAt;
        string lastDisableNonce;

        static bool s_checkedUnityAppActive;
        static PropertyInfo s_unityIsAppActiveProp;
        static bool s_domainReloading;
        static double s_domainReloadingAt;

        // Native plugin for window position monitoring (works during Unity UI thread blocking)
        bool nativePluginRunning;
        string lastPublishedDragText;
        double lastPublishedDragAt;

#if UNITY_EDITOR_WIN
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        const int SM_XVIRTUALSCREEN = 76;
        const int SM_YVIRTUALSCREEN = 77;
        const int SM_CXVIRTUALSCREEN = 78;
        const int SM_CYVIRTUALSCREEN = 79;

        static int GetForegroundPid()
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(h, out var pid);
            return (int)pid;
        }
#endif

        int GetForegroundPidCached(double now)
        {
            // Foreground pid probing can be somewhat expensive on macOS (osascript),
            // so keep a short cache and only re-probe periodically.
            if (now < nextForegroundProbeAt) return lastForegroundPid;

            nextForegroundProbeAt = now + 0.1;
            var pid = TryGetForegroundPidPlatform();
            // On macOS this can fail (permissions / osascript errors). In that case,
            // keep the last known value to avoid aggressive auto-hide behavior.
            if (pid != 0) lastForegroundPid = pid;
            return lastForegroundPid;
        }

        static int TryGetForegroundPidPlatform()
        {
#if UNITY_EDITOR_WIN
            return GetForegroundPid();
#elif UNITY_EDITOR_OSX
            // NOTE: Best-effort. We avoid native plugins here; this is only used to prevent
            // auto-hiding when the user clicks into the Tauri window.
            //
            // Returns the frontmost application's PID.
            // osascript: tell System Events to get unix id of first process whose frontmost is true
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    Arguments = "-e \"tell application \\\"System Events\\\" to get unix id of first process whose frontmost is true\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi);
                if (p == null) return 0;
                if (!p.WaitForExit(250)) return 0;
                var s = (p.StandardOutput?.ReadToEnd() ?? "").Trim();
                return int.TryParse(s, out var pid) ? pid : 0;
            }
            catch
            {
                return 0;
            }
#else
            return 0;
#endif
        }

        [MenuItem("Tools/Unity ACP Client (IPC Sync)")]
        static void OpenMenu() => OpenWindow();

        [MenuItem("Tools/Unity ACP Client (IPC Sync) - Show")]
        static void ShowMenu()
        {
            var w = OpenWindow();
            w.RequestShow();
        }

        [MenuItem("Tools/Unity ACP Client (IPC Sync) - Hide")]
        static void HideMenu()
        {
            var w = OpenWindow();
            w.userHidden = true;
            try { SessionState.SetBool(SessionKey_UserHidden, w.userHidden); } catch { }
            try { w.syncIpc?.WriteFlags(visible: false, active: false); } catch { }
            w.Repaint();
        }

        [MenuItem("Tools/Unity ACP Client (IPC Sync) - Debug - Dump State")]
        static void DumpStateMenu()
        {
            var w = OpenWindow();
            w.DumpState();
        }

        static CodelyWebUiIpcSyncWindow OpenWindow()
        {
            var w = GetWindow<CodelyWebUiIpcSyncWindow>();
            w.titleContent = new GUIContent("AI Agent (IPC)");
            w.Show();
            w.Focus();
            return w;
        }

        void OnEnable()
        {
            // Avoid duplicate subscriptions across domain reload / window recreation.
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;

            AssemblyReloadEvents.beforeAssemblyReload -= PersistForDomainReload;
            AssemblyReloadEvents.beforeAssemblyReload += PersistForDomainReload;

            EditorApplication.quitting -= HandleQuit;
            EditorApplication.quitting += HandleQuit;

            // Global drag tracking: we can't rely on this window's OnGUI for drops because the tauri window sits above it.
            EditorApplication.projectWindowItemOnGUI -= OnProjectWindowItemGUI;
            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
            EditorApplication.hierarchyWindowItemOnGUI -= OnHierarchyWindowItemOnGUI;
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyWindowItemOnGUI;
            SceneView.duringSceneGui -= OnSceneViewGUI;
            SceneView.duringSceneGui += OnSceneViewGUI;

            TryRestoreFromDomainReload();
            try { userHidden = SessionState.GetBool(SessionKey_UserHidden, false); } catch { userHidden = false; }
            StartIfNeeded();

            // If we successfully reloaded scripts and this window is alive again, clear the reload marker.
            s_domainReloading = false;
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= PersistForDomainReload;
            EditorApplication.quitting -= HandleQuit;

            EditorApplication.projectWindowItemOnGUI -= OnProjectWindowItemGUI;
            EditorApplication.hierarchyWindowItemOnGUI -= OnHierarchyWindowItemOnGUI;
            SceneView.duringSceneGui -= OnSceneViewGUI;

            // CRITICAL:
            // OnDisable is called for domain reloads and various editor layout/window lifecycle events.
            // Never kill external processes (tauri-ui / server) here, otherwise the webview refreshes and flickers.
            // Users can explicitly Stop/Restart; we hard-stop on Editor quitting only.
            try { syncIpc?.Dispose(); } catch { }
            syncIpc = null;

            // However: if the user really closed the window, we MUST stop the external processes,
            // otherwise tauri will remain on screen with no owner.
            //
            // We can't reliably distinguish "close" vs "layout rebuild" synchronously, so we delay a tick and
            // check if any instances of this window still exist.
            var disableNonce = Guid.NewGuid().ToString("N");
            lastDisableNonce = disableNonce;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (lastDisableNonce != disableNonce) return;
                    if (s_domainReloading) return;
                    if (EditorApplication.isCompiling) return;
                    if (EditorApplication.isUpdating) return;

                    // If no windows exist, the user closed it: stop processes.
                    if (Resources.FindObjectsOfTypeAll<CodelyWebUiIpcSyncWindow>().Length == 0)
                    {
                        StopAll(forceKill: true);
                    }
                }
                catch
                {
                    // ignore
                }
            };
        }

        void OnDestroy()
        {
            // When the user closes the EditorWindow tab, Unity will destroy the ScriptableObject.
            // In this case we DO want to stop external processes to avoid leaving an orphan tauri window.
            try
            {
                if (s_domainReloading) return;
                if (EditorApplication.isCompiling) return;
                StopAll(forceKill: true);
            }
            catch
            {
                // ignore
            }
        }

        void OnFocus()
        {
            pendingAutoHide = false;
            pendingAutoHideAt = 0;
            lastFocusedAt = EditorApplication.timeSinceStartup;

            // Auto-show only if the user didn't explicitly hide it.
            if (!userHidden)
            {
                RequestShow();
            }
        }

        void OnLostFocus()
        {
            // Don't hide immediately here because the user might be clicking into the tauri-ui window,
            // and the OS foreground pid can lag by a frame. We schedule a hide and let Tick confirm.
            pendingAutoHide = true;
            pendingAutoHideAt = EditorApplication.timeSinceStartup;

            // Drop focus smoothing so we react promptly when switching to other Unity windows/tabs.
            lastFocusedAt = -99999;

            Repaint();
        }

        void HandleQuit()
        {
            StopAll(forceKill: true);
        }

        void PersistForDomainReload()
        {
            s_domainReloading = true;
            s_domainReloadingAt = EditorApplication.timeSinceStartup;
            try
            {
                if (IsProcessAlive(serveProcess)) SessionState.SetInt(SessionKey_ServerPid, serveProcess.Id);
                if (IsProcessAlive(uiProcess)) SessionState.SetInt(SessionKey_UiPid, uiProcess.Id);
                if (!string.IsNullOrWhiteSpace(ipcPath)) SessionState.SetString(SessionKey_IpcPath, ipcPath);
                SessionState.SetBool(SessionKey_UserHidden, userHidden);
                SessionState.SetBool(SessionKey_Detached, detached);
            }
            catch
            {
                // ignore
            }
        }

        static bool IsUnityApplicationActive()
        {
            try
            {
                if (!s_checkedUnityAppActive)
                {
                    s_checkedUnityAppActive = true;
                    var t = Type.GetType("UnityEditorInternal.InternalEditorUtility, UnityEditor");
                    s_unityIsAppActiveProp = t?.GetProperty("isApplicationActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }

                if (s_unityIsAppActiveProp != null && s_unityIsAppActiveProp.PropertyType == typeof(bool))
                {
                    return (bool)s_unityIsAppActiveProp.GetValue(null, null);
                }
            }
            catch
            {
                // ignore
            }

            // Fallback: assume active (we still gate by EditorWindow focus).
            return true;
        }

        static bool IsRectSanePx(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return false;
            if (w > 30000 || h > 30000) return false;

#if UNITY_EDITOR_WIN
            var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            if (vw <= 0 || vh <= 0) return true;

            const int margin = 512;
            return (x + w) > (vx - margin) &&
                   x < (vx + vw + margin) &&
                   (y + h) > (vy - margin) &&
                   y < (vy + vh + margin);
#else
            return true;
#endif
        }

        static bool TryComputeSaneRectPx(Rect rectScreenPoints, out int xPx, out int yPx, out int wPx, out int hPx, out float usedScale)
        {
            xPx = yPx = wPx = hPx = 0;
            usedScale = 1f;

            // GUIToScreenPoint can be "points" or "pixels" depending on platform / DPI / Unity version.
            // Try both and pick the one that fits the current virtual screen bounds.
            var ppp = EditorGUIUtility.pixelsPerPoint;

            var xa = Mathf.RoundToInt(rectScreenPoints.xMin * ppp);
            var ya = Mathf.RoundToInt(rectScreenPoints.yMin * ppp);
            var wa = Mathf.Max(1, Mathf.RoundToInt(rectScreenPoints.width * ppp));
            var ha = Mathf.Max(1, Mathf.RoundToInt(rectScreenPoints.height * ppp));
            var okA = IsRectSanePx(xa, ya, wa, ha);

            var xb = Mathf.RoundToInt(rectScreenPoints.xMin);
            var yb = Mathf.RoundToInt(rectScreenPoints.yMin);
            var wb = Mathf.Max(1, Mathf.RoundToInt(rectScreenPoints.width));
            var hb = Mathf.Max(1, Mathf.RoundToInt(rectScreenPoints.height));
            var okB = IsRectSanePx(xb, yb, wb, hb);

            if (okA && !okB)
            {
                usedScale = ppp;
                xPx = xa; yPx = ya; wPx = wa; hPx = ha;
                return true;
            }

            if (okB && !okA)
            {
                usedScale = 1f;
                xPx = xb; yPx = yb; wPx = wb; hPx = hb;
                return true;
            }

            if (okA && okB)
            {
                usedScale = ppp;
                xPx = xa; yPx = ya; wPx = wa; hPx = ha;
                return true;
            }

            return false;
        }

        static bool TryGetFallbackRectPx(out int xPx, out int yPx, out int wPx, out int hPx)
        {
            xPx = yPx = wPx = hPx = 0;
#if UNITY_EDITOR_WIN
            try
            {
                var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
                var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
                var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
                if (vw <= 0 || vh <= 0) return false;

                wPx = Mathf.Min(1200, vw);
                hPx = Mathf.Min(800, vh);
                xPx = vx + (vw - wPx) / 2;
                yPx = vy + (vh - hPx) / 2;
                return true;
            }
            catch
            {
                return false;
            }
#else
            try
            {
                // Best-effort on macOS/Linux: center a sane default rect on the main display.
                // This is used only when we have no valid OnGUI rect yet (e.g. first Show click).
                var d = Display.main;
                var sw = d != null ? d.systemWidth : 0;
                var sh = d != null ? d.systemHeight : 0;
                if (sw <= 0 || sh <= 0)
                {
                    // Fallback: use current resolution.
                    var r = Screen.currentResolution;
                    sw = r.width;
                    sh = r.height;
                }
                if (sw <= 0 || sh <= 0) return false;

                wPx = Mathf.Min(1200, sw);
                hPx = Mathf.Min(800, sh);
                xPx = (sw - wPx) / 2;
                yPx = (sh - hPx) / 2;
                return true;
            }
            catch
            {
                return false;
            }
#endif
        }

        void RequestShow()
        {
            userHidden = false;
            var now = EditorApplication.timeSinceStartup;
            // "Show" must be a reliable rescue even under heavy editor stalls.
            // Keep it long enough that the next few EditorApplication.update ticks can publish visible=1 + a sane rect.
            forceShowUntil = now + 10.0;
            lastFocusedAt = now;
            lastUnityAppActiveAt = now;
            try { SessionState.SetBool(SessionKey_UserHidden, userHidden); } catch { }

            // If the user clicks Show/Attach while detached, implicitly attach.
            if (detached)
            {
                detached = false;
                try { SessionState.SetBool(SessionKey_Detached, detached); } catch { }
                TrySignalAttachToTauri();
            }

            if (debugLogs)
            {
                UnityEngine.Debug.Log($"[IPC Sync] RequestShow: now={now:0.000} forceShowFor={forceShowUntil - now:0.000}s ipc={ipcPath}");
            }

            // Keep lastGoodRect across focus changes: during fast tab switches / docking Unity can fail to report a
            // valid rect for a frame. If we clear the cache here, we'd be forced to "invent" a rect (flash).
            wroteRectOnce = false;
            if (hasLastGoodRect && !IsRectSanePx(lastGoodX, lastGoodY, lastGoodW, lastGoodH))
            {
                hasLastGoodRect = false;
            }

            try
            {
                EnsureSyncIpc();

                long ownerHwnd = 0;
#if UNITY_EDITOR_WIN
                if (Win32WindowEmbedding.TryGetEditorWindowHwnd(this, out var owner, out _))
                {
                    ownerHwnd = owner.ToInt64();
                    if (ownerHwnd != 0) lastOwnerHwnd = ownerHwnd;
                }
#endif
                if (ownerHwnd == 0) ownerHwnd = lastOwnerHwnd;

                var rectOk = embedRectScreenPoints.width > 1f && embedRectScreenPoints.height > 1f;

                if (rectOk && TryComputeSaneRectPx(embedRectScreenPoints, out var xPx, out var yPx, out var wPx, out var hPx, out _))
                {
                    hasLastGoodRect = true;
                    lastGoodX = xPx; lastGoodY = yPx; lastGoodW = wPx; lastGoodH = hPx;
                    syncIpc?.WriteRect(xPx, yPx, wPx, hPx, visible: true, active: true, ownerHwnd: ownerHwnd);
                    wroteRectOnce = true;
                }
                else if (hasLastGoodRect && IsRectSanePx(lastGoodX, lastGoodY, lastGoodW, lastGoodH))
                {
                    // No fresh rect yet; show at the last known good position (never invent a new one).
                    syncIpc?.WriteRect(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, active: true, ownerHwnd: ownerHwnd);
                    wroteRectOnce = true;
                }
                else
                {
                    // Don't invent a rect. If we have none yet, keep the window hidden until OnGUI publishes a real one.
                    syncIpc?.WriteFlags(visible: true, active: true);
                }
            }
            catch
            {
                // ignore
            }

            StartIfNeeded();
            Focus();
        }

        void TryRestoreFromDomainReload()
        {
            try
            {
                var serverPid = SessionState.GetInt(SessionKey_ServerPid, 0);
                var uiPid = SessionState.GetInt(SessionKey_UiPid, 0);
                var savedIpcPath = SessionState.GetString(SessionKey_IpcPath, null);
                detached = SessionState.GetBool(SessionKey_Detached, false);

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

                if (GUILayout.Button("Force Restart", EditorStyles.toolbarButton, GUILayout.Width(95)))
                {
                    ForceRestartServer();
                }

                if (GUILayout.Button("Stop", EditorStyles.toolbarButton, GUILayout.Width(50)))
                {
                    StopAll(forceKill: true);
                }

                if (GUILayout.Button(userHidden ? "Show" : "Hide", EditorStyles.toolbarButton, GUILayout.Width(50)))
                {
                    if (userHidden)
                    {
                        RequestShow();
                    }
                    else
                    {
                        userHidden = true;
                        try { SessionState.SetBool(SessionKey_UserHidden, userHidden); } catch { }
                        try { syncIpc?.WriteFlags(visible: false, active: false); } catch { }
                    }
                }

                if (GUILayout.Button(detached ? "Attach" : "Detach", EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    detached = !detached;
                    try { SessionState.SetBool(SessionKey_Detached, detached); } catch { }

                    if (detached)
                    {
                        TrySignalDetachToTauri();

                        // Stop pushing rect/visibility updates; let the Tauri window become independent.
                        try { syncIpc?.WriteFlags(visible: false, active: false); } catch { }
                        // Ensure we don't keep repainting heavily.
                        wroteRectOnce = false;
                    }
                    else
                    {
                        TrySignalAttachToTauri();

                        // Resume syncing + force show.
                        RequestShow();
                    }
                }

                if (GUILayout.Button("Open Browser", EditorStyles.toolbarButton, GUILayout.Width(90)))
                {
                    Application.OpenURL(DefaultUrl);
                }

                if (GUILayout.Button("Log CWD", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    UnityEngine.Debug.Log($"[IPC Sync] server cwd={GuessWorkspaceRoot()}");
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
            // IMPORTANT:
            // Leave a small inset so Unity's resize splitters / window edges are always draggable
            // and never accidentally hit-test on the tauri window edge.
            var publishRect = embedRect;
            {
                const float EdgeInsetPx = 8f;
                var inset = EdgeInsetPx / Mathf.Max(1e-3f, EditorGUIUtility.pixelsPerPoint);
                if (publishRect.width > inset * 2 + 16 && publishRect.height > inset * 2 + 16)
                {
                    publishRect = Rect.MinMaxRect(
                        publishRect.xMin + inset,
                        publishRect.yMin + inset,
                        publishRect.xMax - inset,
                        publishRect.yMax - inset);
                }
            }

            var tl = GUIUtility.GUIToScreenPoint(new Vector2(publishRect.xMin, publishRect.yMin));
            var br = GUIUtility.GUIToScreenPoint(new Vector2(publishRect.xMax, publishRect.yMax));
            embedRectScreenPoints = Rect.MinMaxRect(tl.x, tl.y, br.x, br.y);

            // Drag-and-drop bridge: allow dropping Unity objects into the agent window area.
            // We translate the drop into a "deeplink" text and forward it to the Tauri UI via IPC.
            TryHandleUnityDragDrop(publishRect);

            if (!wroteRectOnce)
            {
                GUI.Box(embedRect, "IPC Sync: waiting for first rect publish...");
            }

            // Rescue button: when Tauri gets buried after move/resize, this becomes clickable (because Tauri is no longer on top)
            // and will force the window to show + raise again.
            // When Tauri is correctly on top, it will cover this button.
            {
                var w = 220f;
                var h = 40f;
                var r = new Rect(
                    embedRect.x + (embedRect.width - w) * 0.5f,
                    embedRect.y + (embedRect.height - h) * 0.5f,
                    w,
                    h);

                if (GUI.Button(r, detached ? "Attach" : "Show Agent Window"))
                {
                    if (detached)
                    {
                        detached = false;
                        try { SessionState.SetBool(SessionKey_Detached, detached); } catch { }
                        TrySignalAttachToTauri();
                    }

                    RequestShow();
                }
            }
        }

        void TryHandleUnityDragDrop(Rect publishRect)
        {
            try
            {
                var e = Event.current;
                if (e == null) return;

                // Only treat drops inside the published rect area (leave toolbar free).
                if (!publishRect.Contains(e.mousePosition)) return;

                if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;

                // Let the user know dropping is supported.
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    if (TryBuildDropDeeplinkText(out var deeplinkText))
                    {
                        PublishDropDeeplinkToTauri(deeplinkText);
                    }
                }

                e.Use();
            }
            catch
            {
                // ignore (drag should never break OnGUI)
            }
        }

        void OnProjectWindowItemGUI(string guid, Rect rect) => TryCaptureUnityDragForTauri();

        void OnHierarchyWindowItemOnGUI(int instanceId, Rect rect) => TryCaptureUnityDragForTauri();

        void OnSceneViewGUI(SceneView sceneView) => TryCaptureUnityDragForTauri();

        void TryCaptureUnityDragForTauri()
        {
            // When dragging from Unity to the Tauri window, Unity is the drag source and the Tauri webview is the drop target.
            // This means our EditorWindow OnGUI will not receive DragPerform events (the Tauri window is on top).
            // So we capture Unity's current drag payload while it is still inside Unity and forward it via IPC.
            try
            {
                var e = Event.current;
                if (e == null) return;
                if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;

                if (!TryBuildDropDeeplinkText(out var deeplinkText)) return;

                var now = EditorApplication.timeSinceStartup;
                if (string.Equals(deeplinkText, lastPublishedDragText, StringComparison.Ordinal) && (now - lastPublishedDragAt) < 0.25)
                {
                    return;
                }

                lastPublishedDragText = deeplinkText;
                lastPublishedDragAt = now;
                PublishDropDeeplinkToTauri(deeplinkText);
            }
            catch
            {
                // ignore (must never break editor drag UX)
            }
        }

        static bool TryBuildDropDeeplinkText(out string deeplinkText)
        {
            deeplinkText = null;
            try
            {
                var sb = new StringBuilder(256);
                var any = false;

                // Unity object references (assets, scene objects, components, etc.)
                var objs = DragAndDrop.objectReferences;
                if (objs != null)
                {
                    foreach (var o in objs)
                    {
                        if (o == null) continue;
                        var link = BuildUnityObjectDeeplink(o);
                        if (string.IsNullOrWhiteSpace(link)) continue;
                        if (any) sb.Append('\n');
                        sb.Append(link);
                        any = true;
                    }
                }

                // External file drops (best-effort)
                var paths = DragAndDrop.paths;
                if (paths != null)
                {
                    foreach (var p in paths)
                    {
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        var link = BuildFilePathDeeplink(p);
                        if (string.IsNullOrWhiteSpace(link)) continue;
                        if (any) sb.Append('\n');
                        sb.Append(link);
                        any = true;
                    }
                }

                if (!any) return false;
                deeplinkText = sb.ToString();
                return !string.IsNullOrWhiteSpace(deeplinkText);
            }
            catch
            {
                deeplinkText = null;
                return false;
            }
        }

        static string BuildUnityObjectDeeplink(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            try
            {
                string gid = null;
                try
                {
                    // Stable-ish reference that can represent both assets and scene objects.
                    gid = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
                }
                catch
                {
                    gid = null;
                }

                var name = obj.name ?? string.Empty;
                var type = obj.GetType().FullName ?? obj.GetType().Name;

                var assetPath = string.Empty;
                var assetGuid = string.Empty;
                try
                {
                    assetPath = AssetDatabase.GetAssetPath(obj) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(assetPath))
                    {
                        assetGuid = AssetDatabase.AssetPathToGUID(assetPath) ?? string.Empty;
                    }
                }
                catch
                {
                    assetPath = string.Empty;
                    assetGuid = string.Empty;
                }

                // Simple deeplink format (text-only). Web UI decides how to render/resolve.
                // Example:
                // unity://object?gid=...&guid=...&path=...&name=...&type=...
                var sb = new StringBuilder(256);
                sb.Append("unity://object?");
                var first = true;

                void Add(string k, string v)
                {
                    if (string.IsNullOrWhiteSpace(v)) return;
                    if (!first) sb.Append('&');
                    first = false;
                    sb.Append(k);
                    sb.Append('=');
                    sb.Append(Uri.EscapeDataString(v));
                }

                Add("gid", gid);
                Add("guid", assetGuid);
                Add("path", assetPath);
                Add("name", name);
                Add("type", type);

                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        static string BuildFilePathDeeplink(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                // Keep it as a deeplink-like text, not a raw OS path.
                // Example: unity://file?path=C%3A%5Cfoo%5Cbar.png
                return "unity://file?path=" + Uri.EscapeDataString(path);
            }
            catch
            {
                return null;
            }
        }

        void PublishDropDeeplinkToTauri(string deeplinkText)
        {
            if (string.IsNullOrWhiteSpace(deeplinkText)) return;
            try
            {
                EnsureSyncIpc();
                if (syncIpc == null || string.IsNullOrWhiteSpace(ipcPath)) return;

                // Sidecar file: easiest cross-process payload channel.
                // Tauri watches dropSeq and reads this file when it changes.
                var dropPath = ipcPath + ".drop";
                File.WriteAllText(dropPath, deeplinkText, Encoding.UTF8);

                lastDropSeq = unchecked(lastDropSeq + 1);
                syncIpc.WriteDropSeq(lastDropSeq);

                if (debugLogs)
                {
                    UnityEngine.Debug.Log($"[IPC Sync] drop: seq={lastDropSeq} file={dropPath} bytes={Encoding.UTF8.GetByteCount(deeplinkText)}");
                }
            }
            catch (Exception ex)
            {
                if (debugLogs)
                {
                    UnityEngine.Debug.LogWarning($"[IPC Sync] drop publish failed: {ex.Message}");
                }
            }
        }

        void Tick()
        {
            var now = EditorApplication.timeSinceStartup;

            // Keep OnGUI running so GUIToScreenPoint + embed rect stay fresh (otherwise we never publish).
            // On macOS, repainting every editor tick is noticeably expensive; throttle it.
#if UNITY_EDITOR_OSX
            if (now >= nextRepaintAt)
            {
                nextRepaintAt = now + 1.0 / 20.0; // ~20fps is enough for window-follow + input feel
                Repaint();
            }
#else
            Repaint();
#endif

            // If the server process died, surface diagnostics (helps on macOS where PATH/cwd issues are common).
            TryCaptureServerExitIfAny();
            TryCaptureUiExitIfAny();

            var portOpen = IsWebUiPortOpenCached(now);
            var serverAlive = IsProcessAlive(serveProcess);
            var serverLabel = serverAlive ? (portOpen ? "Running" : "Starting") : (portOpen ? "External" : "Stopped");
            var uiLabel = IsProcessAlive(uiProcess) ? "Tauri" : "Stopped";

            try
            {
                // Ensure IPC exists early so we always launch Tauri with a valid IPC_PATH/NAME.
                EnsureSyncIpc();

                var unityPid0 = Process.GetCurrentProcess().Id;
                var tauriPid0 = IsProcessAlive(uiProcess) ? uiProcess.Id : 0;
                if (tauriPid0 != 0) lastTauriPid = tauriPid0;
                var fgPid0 = GetForegroundPidCached(now);
                var tauriForeground0 = tauriPid0 != 0 && fgPid0 == tauriPid0;
                if (!tauriForeground0 && lastTauriPid != 0 && fgPid0 == lastTauriPid)
                {
                    tauriForeground0 = true;
                }

                // If we lost focus to another Unity tab/window, hide immediately (unless the user clicked into tauri-ui).
                if (pendingAutoHide)
                {
                    // If we can't reliably determine the foreground process (common on macOS without permissions),
                    // do NOT auto-hide. This preserves usability even when foreground PID probing is unavailable.
                    if (fgPid0 == 0)
                    {
                        pendingAutoHide = false;
                        return;
                    }

                    // Cancel auto-hide if the user is actually interacting with tauri-ui.
                    if (tauriForeground0)
                    {
                        pendingAutoHide = false;
                    }
                    else if ((now - pendingAutoHideAt) >= 0.016) // ~1 frame grace
                    {
                        pendingAutoHide = false;
                        try { syncIpc?.WriteFlags(visible: false, active: false); } catch { }
                        wroteRectOnce = false;
                        lastDesiredVisible = false;
                        lastDesiredActive = false;

                        if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                        {
                            nextDebugLogAt = EditorApplication.timeSinceStartup + 0.5;
                            UnityEngine.Debug.Log($"[IPC Sync] auto-hide (lost focus): unityPid={unityPid0} fgPid={fgPid0} tauriPid={tauriPid0} ipc={ipcPath}");
                        }

                        return;
                    }
                }
                // end auto-hide handling

                // Keep server alive (only start if port isn't already served).
                if (!serverAlive && !portOpen && now >= nextServerStartAt)
                {
                    nextServerStartAt = now + 2.0; // avoid spawn spam on failure
                    StartServer();
                    nextServerProbeAt = 0; // re-probe soon
                }

                // Launch Tauri if not running (unless user explicitly hid it).
                if (!userHidden && !IsProcessAlive(uiProcess) && portOpen && now >= nextUiStartAt)
                {
                    nextUiStartAt = now + 1.0;
                    StartTauri();
                }

                // Detached mode: keep tauri running, but do not publish rect/visibility updates.
                // The UI becomes a standalone window.
                if (detached)
                {
                    if (IsProcessAlive(uiProcess))
                    {
                        // Best-effort: tell UI to become independent.
                        TrySignalDetachToTauri();
                    }
                    return;
                }

                var rectOk = embedRectScreenPoints.width > 1f && embedRectScreenPoints.height > 1f;

                // Visibility / layering policy (per user request):
                // - When Unity (or tauri-ui) is the active foreground app => tauri window may be visible.
                // - When THIS EditorWindow is focused (or during "Show" rescue window) => tauri window must be on top.
                // - When Unity is not foreground (other app active) => tauri must hide (no global always-on-top).
                // Focus can be quirky across platforms when external windows (Tauri) sit on top.
                // For macOS we rely on focusedWindow only; `hasFocus` can remain true even when dock/tab focus moved.
                var unityFocusedRaw = EditorWindow.focusedWindow == this;
#if UNITY_EDITOR_WIN
                unityFocusedRaw = unityFocusedRaw || hasFocus;
#endif
                if (unityFocusedRaw) lastFocusedAt = now;
                // Focus can jitter during dock/move/resize; smooth it slightly.
                var unityFocused = unityFocusedRaw || (now - lastFocusedAt) < 0.2;

                var unityAppActive = IsUnityApplicationActive();
                var fgPid = fgPid0;
                var unityPid = unityPid0;
                var tauriPid = tauriPid0;
                var tauriForeground = tauriForeground0;
                var unityForeground = fgPid != 0 && fgPid == unityPid;
                var unityActive = unityAppActive || unityForeground;

#if !UNITY_EDITOR_WIN
                // On macOS (and some Unity forks), InternalEditorUtility.isApplicationActive can be unreliable,
                // and foreground PID probing can be flaky (permissions / helper processes).
                // If THIS window is focused, treat Unity as active so IPC Sync remains usable.
                if (!unityActive && unityFocused)
                {
                    unityActive = true;
                }
#endif

                // Requested behavior:
                // - When switching to other Unity windows/tabs, hide.
                // - When this agent window is focused, show.
                // - If the user clicks into tauri-ui, keep it visible so it can be interacted with.
                // - If Unity isn't active and tauri-ui isn't foreground, hide (never cover other apps).
                //
                // On macOS, when focus leaves this EditorWindow (focusedWindow changes), we must hide promptly.
                // We still keep it visible if the user is actively interacting with the Tauri window.
                // Force-visible window rescue: after RequestShow we keep the window visible even if focus detection
                // is temporarily wrong (common on macOS when external windows exist).
                var forceShow = !userHidden && now <= forceShowUntil;

                var visibleWanted = !userHidden && (forceShow || tauriForeground || unityActive);
                var activeWanted = !userHidden && (forceShow || tauriForeground || (unityActive && unityFocused));

                lastDesiredVisible = visibleWanted;
                lastDesiredActive = activeWanted;

                // Determine owner HWND so the Tauri window stays above this EditorWindow whenever it's visible.
                // We intentionally avoid aggressive Z-order manipulation on the Tauri side; ownership is the stable solution.
                long ownerHwnd = 0;
#if UNITY_EDITOR_WIN
                if (visibleWanted && Win32WindowEmbedding.TryGetEditorWindowHwnd(this, out var owner, out _))
                {
                    ownerHwnd = owner.ToInt64();
                }
#endif
                // Owner HWND can be temporarily unavailable during docking/move/resize.
                // Never clear it on a transient failure; otherwise the Tauri window can lose its "owned" layering and disappear.
                if (ownerHwnd != 0)
                {
                    lastOwnerHwnd = ownerHwnd;
                }
                else
                {
                    ownerHwnd = lastOwnerHwnd;
                }

                if (!visibleWanted)
                {
#if UNITY_EDITOR_OSX
                    // Throttle visibility updates too (avoid hammering shared memory every tick).
                    if (now >= nextPublishAt && (lastPublishedVisible || lastPublishedActive))
                    {
                        nextPublishAt = now + 1.0 / 20.0;
                        try { syncIpc?.WriteFlags(visible: false, active: false); } catch { }
                        lastPublishedVisible = false;
                        lastPublishedActive = false;
                        lastPublishedOwnerHwnd = 0;
                    }
#else
                    try { syncIpc?.WriteFlags(visible: false, active: false); } catch { }
#endif
                    wroteRectOnce = false;
                    if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                    {
                        nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                        UnityEngine.Debug.Log($"[IPC Sync] hide: userHidden={userHidden} unityFocused={unityFocusedRaw} unityFocusedSmoothed={unityFocused} unityAppActive={unityAppActive} unityFg={unityForeground} unityActive={unityActive} tauriFg={tauriForeground} fgPid={fgPid} unityPid={unityPid} tauriPid={tauriPid} ipc={ipcPath} seq={(syncIpc != null ? syncIpc.LastSeq : 0)}");
                    }
                    return;
                }

                // If rect becomes temporarily invalid (e.g. 1x1 during dock/resize), keep using lastGoodRect.
                if (!rectOk && hasLastGoodRect)
                {
                    if (!IsRectSanePx(lastGoodX, lastGoodY, lastGoodW, lastGoodH))
                    {
                        hasLastGoodRect = false;
                    }
                    else
                    {
#if UNITY_EDITOR_OSX
                        // Throttle rect publishing on macOS.
                        if (now >= nextPublishAt && ShouldPublishRectMac(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, active: activeWanted, ownerHwnd: ownerHwnd))
                        {
                            nextPublishAt = now + 1.0 / 20.0;
                            syncIpc.WriteRect(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
                            CacheLastPublishedRectMac(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
                        }
#else
                        syncIpc.WriteRect(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
#endif
                        wroteRectOnce = true;
                        lastError = null;

                        if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                        {
                            nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                            UnityEngine.Debug.Log($"[IPC Sync] rect transient; using lastGood rect: seq={syncIpc.LastSeq} rect=({lastGoodX},{lastGoodY},{lastGoodW},{lastGoodH}) visible=1 active={(activeWanted ? 1 : 0)} owner=0x{ownerHwnd:X} unityAppActive={unityAppActive} unityFg={unityForeground} unityFocused={unityFocused} tauriFg={tauriForeground} fgPid={fgPid} ipc={ipcPath}");
                        }
                        return;
                    }
                }

                if (!rectOk && !hasLastGoodRect)
                {
                    // If we don't have any sane rect yet, Tauri will stay hidden (it requires w/h > 0).
                    // Use a fallback rect while we're force-showing or before we've successfully published any rect.
                    if ((now <= forceShowUntil || !wroteRectOnce) && TryGetFallbackRectPx(out var fx, out var fy, out var fw, out var fh))
                    {
                        hasLastGoodRect = true;
                        lastGoodX = fx; lastGoodY = fy; lastGoodW = fw; lastGoodH = fh;
#if UNITY_EDITOR_OSX
                        if (now >= nextPublishAt && ShouldPublishRectMac(fx, fy, fw, fh, visible: true, active: activeWanted, ownerHwnd: ownerHwnd))
                        {
                            nextPublishAt = now + 1.0 / 20.0;
                            syncIpc.WriteRect(fx, fy, fw, fh, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
                            CacheLastPublishedRectMac(fx, fy, fw, fh, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
                        }
#else
                        syncIpc.WriteRect(fx, fy, fw, fh, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
#endif
                        wroteRectOnce = true;
                        lastError = null;
                        if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                        {
                            nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                            UnityEngine.Debug.Log($"[IPC Sync] rect not ready; using fallback rect: seq={syncIpc.LastSeq} rect=({fx},{fy},{fw},{fh}) visible=1 active={(activeWanted ? 1 : 0)} unityAppActive={unityAppActive} unityFg={unityForeground} unityFocused={unityFocused} tauriFg={tauriForeground} fgPid={fgPid} ipc={ipcPath}");
                        }
                    }
                    else
                    {
                        // Keep the last mapping rect (if any) and only update visibility/topmost flags.
#if UNITY_EDITOR_OSX
                        if (now >= nextPublishAt && (lastPublishedVisible != true || lastPublishedActive != activeWanted))
                        {
                            nextPublishAt = now + 1.0 / 20.0;
                            try { syncIpc?.WriteFlags(visible: true, active: activeWanted); } catch { }
                            lastPublishedVisible = true;
                            lastPublishedActive = activeWanted;
                            lastPublishedOwnerHwnd = ownerHwnd;
                        }
#else
                        try { syncIpc?.WriteFlags(visible: true, active: activeWanted); } catch { }
#endif
                        wroteRectOnce = false;
                        if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                        {
                            nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                            UnityEngine.Debug.Log($"[IPC Sync] rect not ready yet (no fallback): visible=1 active={(activeWanted ? 1 : 0)} unityAppActive={unityAppActive} unityFg={unityForeground} unityFocused={unityFocused} tauriFg={tauriForeground} fgPid={fgPid} rect=({embedRectScreenPoints.xMin:0.##},{embedRectScreenPoints.yMin:0.##},{embedRectScreenPoints.width:0.##},{embedRectScreenPoints.height:0.##}) ppp={EditorGUIUtility.pixelsPerPoint:0.##} ipc={ipcPath} seq={(syncIpc != null ? syncIpc.LastSeq : 0)}");
                        }
                    }
                    return;
                }

                if (!TryComputeSaneRectPx(embedRectScreenPoints, out var xPx, out var yPx, out var wPx, out var hPx, out var usedScale))
                {
                    // Occasionally during docking/move/resize Unity reports garbage screen coords (still with non-trivial size).
                    // Never "poison" lastGoodRect with these values — keep using lastGoodRect if we have one.
                    if (hasLastGoodRect && !IsRectSanePx(lastGoodX, lastGoodY, lastGoodW, lastGoodH))
                    {
                        hasLastGoodRect = false;
                    }

                    if (hasLastGoodRect)
                    {
#if UNITY_EDITOR_OSX
                        if (now >= nextPublishAt && ShouldPublishRectMac(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, active: activeWanted, ownerHwnd: ownerHwnd))
                        {
                            nextPublishAt = now + 1.0 / 20.0;
                            syncIpc.WriteRect(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
                            CacheLastPublishedRectMac(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
                        }
#else
                        syncIpc.WriteRect(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
#endif
                        wroteRectOnce = true;
                        lastError = null;
                        if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                        {
                            nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                            UnityEngine.Debug.Log($"[IPC Sync] rect insane; using lastGood rect: seq={syncIpc.LastSeq} rect=({lastGoodX},{lastGoodY},{lastGoodW},{lastGoodH}) visible=1 active={(activeWanted ? 1 : 0)} owner=0x{ownerHwnd:X} unityAppActive={unityAppActive} unityFg={unityForeground} unityFocused={unityFocused} tauriFg={tauriForeground} fgPid={fgPid} ipc={ipcPath}");
                        }
                        return;
                    }

                    // No sane rect to publish yet; do not invent one. Keep visible state, but don't move.
#if UNITY_EDITOR_OSX
                    if (now >= nextPublishAt && (lastPublishedVisible != true || lastPublishedActive != activeWanted))
                    {
                        nextPublishAt = now + 1.0 / 20.0;
                        try { syncIpc?.WriteFlags(visible: true, active: activeWanted); } catch { }
                        lastPublishedVisible = true;
                        lastPublishedActive = activeWanted;
                        lastPublishedOwnerHwnd = ownerHwnd;
                    }
#else
                    try { syncIpc?.WriteFlags(visible: true, active: activeWanted); } catch { }
#endif
                    wroteRectOnce = false;
                    if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                    {
                        nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                        UnityEngine.Debug.Log($"[IPC Sync] rect insane; skip publish: visible=1 active={(activeWanted ? 1 : 0)} unityAppActive={unityAppActive} unityFg={unityForeground} unityFocused={unityFocused} tauriFg={tauriForeground} fgPid={fgPid} rect=({embedRectScreenPoints.xMin:0.##},{embedRectScreenPoints.yMin:0.##},{embedRectScreenPoints.width:0.##},{embedRectScreenPoints.height:0.##}) ppp={EditorGUIUtility.pixelsPerPoint:0.##} usedScale={usedScale:0.##} ipc={ipcPath} seq={(syncIpc != null ? syncIpc.LastSeq : 0)}");
                    }
                    return;
                }

                // Avoid polluting lastGoodRect with stale mid-drag coords when OnGUI isn't updating.
                // We only commit lastGoodRect when we recently had a GUI pass or we're actively force-showing.
                var onGuiAge = now - lastOnGuiAt;
                var commitLastGood = onGuiAge < 0.2 || unityFocusedRaw || now <= forceShowUntil;
                if (commitLastGood)
                {
                    hasLastGoodRect = true;
                    lastGoodX = xPx; lastGoodY = yPx; lastGoodW = wPx; lastGoodH = hPx;
                }

#if UNITY_EDITOR_OSX
                if (now >= nextPublishAt && ShouldPublishRectMac(xPx, yPx, wPx, hPx, visible: true, active: activeWanted, ownerHwnd: ownerHwnd))
                {
                    nextPublishAt = now + 1.0 / 20.0;
                    syncIpc.WriteRect(xPx, yPx, wPx, hPx, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
                    CacheLastPublishedRectMac(xPx, yPx, wPx, hPx, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
                }
#else
                syncIpc.WriteRect(xPx, yPx, wPx, hPx, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
#endif
                wroteRectOnce = true;
                lastError = null;

                if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                {
                    nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                    UnityEngine.Debug.Log($"[IPC Sync] write rect: seq={syncIpc.LastSeq} x={xPx} y={yPx} w={wPx} h={hPx} visible=1 active={(activeWanted ? 1 : 0)} owner=0x{ownerHwnd:X} unityAppActive={unityAppActive} unityFg={unityForeground} unityFocused={unityFocused} tauriFg={tauriForeground} fgPid={fgPid} ipc={ipcPath} pid={(IsProcessAlive(uiProcess) ? uiProcess.Id : 0)}");
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

            var now = EditorApplication.timeSinceStartup;
            var portOpen = IsPortOpen("127.0.0.1", DefaultPort, 50);

            if (!IsProcessAlive(serveProcess) && !portOpen && now >= nextServerStartAt)
            {
                nextServerStartAt = now + 2.0;
                StartServer();
                nextServerProbeAt = 0;
            }

            // Avoid starting the UI until the server is actually accepting connections.
            // This prevents the webview from getting stuck on ERR_CONNECTION_REFUSED / blank error pages.
            if (!userHidden && !IsProcessAlive(uiProcess) && portOpen && now >= nextUiStartAt)
            {
                nextUiStartAt = now + 1.0;
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

            // Try to start native plugin for window position monitoring
            // This allows Tauri window to follow Unity window even during drag operations
#if UNITY_EDITOR_WIN
            if (!nativePluginRunning)
            {
                if (Win32WindowEmbedding.TryGetEditorWindowHwnd(this, out var ownerHwnd, out _) && ownerHwnd != IntPtr.Zero)
                {
                    if (CodelyWindowSyncNative.TryStart(ownerHwnd.ToInt64(), ipcPath))
                    {
                        nativePluginRunning = true;
                        if (debugLogs)
                        {
                            UnityEngine.Debug.Log($"[IPC Sync] native plugin started: hwnd=0x{ownerHwnd.ToInt64():X} ipc={ipcPath}");
                        }
                    }
                    else if (debugLogs)
                    {
                        UnityEngine.Debug.LogWarning($"[IPC Sync] native plugin failed to start (DLL may be missing), falling back to C# polling");
                    }
                }
            }
#endif

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

                var wd = Environment.GetEnvironmentVariable(ServerCwdEnv);
                if (string.IsNullOrWhiteSpace(wd))
                {
                    wd = GuessWorkspaceRoot();
                }

                // Reset log tails for the new attempt so we don't mix runs.
                lock (serverLogLock)
                {
                    serverStdoutTail = null;
                    serverStderrTail = null;
                }
                var psi = new ProcessStartInfo
                {
                    WorkingDirectory = wd,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
#if UNITY_EDITOR_WIN
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c codely serve web-ui --port {DefaultPort}";
#else
                if (!TryFindCodelyExecutable(out var codelyExe, out var err))
                {
                    lastError = err ?? "codely executable not found";
                    return;
                }
                psi.FileName = codelyExe;
                psi.Arguments = $"serve web-ui --port {DefaultPort}";

                // Unity on macOS often launches with a minimal PATH (missing Homebrew).
                // Ensure child processes can still resolve node/codely deps.
                try
                {
                    var existingPath = psi.EnvironmentVariables["PATH"];
                    var extraPath = ":/opt/homebrew/bin:/usr/local/bin";
                    if (string.IsNullOrWhiteSpace(existingPath))
                    {
                        psi.EnvironmentVariables["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin" + extraPath;
                    }
                    else if (!existingPath.Contains("/opt/homebrew/bin") && !existingPath.Contains("/usr/local/bin"))
                    {
                        psi.EnvironmentVariables["PATH"] = existingPath + extraPath;
                    }
                }
                catch
                {
                    // ignore
                }
#endif

                UnityEngine.Debug.Log($"[IPC Sync] start server: cwd={wd}");
                serveProcess = Process.Start(psi);
                if (serveProcess != null)
                {
                    try
                    {
                        serveProcess.EnableRaisingEvents = true;
                        serveProcess.OutputDataReceived += (_, e) => AppendServerLog(ref serverStdoutTail, e?.Data);
                        serveProcess.ErrorDataReceived += (_, e) => AppendServerLog(ref serverStderrTail, e?.Data);
                        serveProcess.BeginOutputReadLine();
                        serveProcess.BeginErrorReadLine();
                    }
                    catch
                    {
                        // ignore
                    }

                    // If it dies immediately, surface a helpful error.
                    try
                    {
                        if (serveProcess.WaitForExit(250))
                        {
                            var outTail = SafeTrimServerLog(serverStdoutTail);
                            var errTail = SafeTrimServerLog(serverStderrTail);
                            lastError =
                                $"Server exited immediately (code={serveProcess.ExitCode}).\n" +
                                $"cwd={wd}\n" +
                                $"cmd={psi.FileName} {psi.Arguments}\n" +
                                (!string.IsNullOrWhiteSpace(errTail) ? $"stderr:\n{errTail}\n" : "") +
                                (!string.IsNullOrWhiteSpace(outTail) ? $"stdout:\n{outTail}\n" : "");
                            if (debugLogs)
                            {
                                UnityEngine.Debug.LogError("[IPC Sync] " + lastError);
                            }
                            try { serveProcess.Dispose(); } catch { }
                            serveProcess = null;
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            catch (Exception ex)
            {
                lastError = $"Failed to start server: {ex.Message}";
            }
        }

        void TryCaptureServerExitIfAny()
        {
            // NOTE: Called every Tick; keep it cheap.
            var p = serveProcess;
            if (p == null) return;

            var exited = false;
            try { exited = p.HasExited; }
            catch { exited = true; }
            if (!exited) return;

            try
            {
                var code = 0;
                try { code = p.ExitCode; } catch { }

                var outTail = SafeTrimServerLog(serverStdoutTail);
                var errTail = SafeTrimServerLog(serverStderrTail);

                // Avoid overwriting a more specific error set by StartServer immediate-exit path.
                if (string.IsNullOrWhiteSpace(lastError))
                {
                    lastError =
                        $"Server exited (code={code}).\n" +
                        (!string.IsNullOrWhiteSpace(errTail) ? $"stderr:\n{errTail}\n" : "") +
                        (!string.IsNullOrWhiteSpace(outTail) ? $"stdout:\n{outTail}\n" : "");
                    if (debugLogs)
                    {
                        UnityEngine.Debug.LogError("[IPC Sync] " + lastError);
                    }
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { p.Dispose(); } catch { }
                serveProcess = null;
            }
        }

        void TryCaptureUiExitIfAny()
        {
            // NOTE: Called every Tick; keep it cheap.
            var p = uiProcess;
            if (p == null) return;

            var exited = false;
            try { exited = p.HasExited; }
            catch { exited = true; }
            if (!exited) return;

            try
            {
                var code = 0;
                try { code = p.ExitCode; } catch { }

                var outTail = SafeTrimServerLog(uiStdoutTail);
                var errTail = SafeTrimServerLog(uiStderrTail);

                if (string.IsNullOrWhiteSpace(lastError))
                {
                    lastError =
                        $"Tauri exited (code={code}).\n" +
                        (!string.IsNullOrWhiteSpace(errTail) ? $"stderr:\n{errTail}\n" : "") +
                        (!string.IsNullOrWhiteSpace(outTail) ? $"stdout:\n{outTail}\n" : "");
                    if (debugLogs)
                    {
                        UnityEngine.Debug.LogError("[IPC Sync] " + lastError);
                    }
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { p.Dispose(); } catch { }
                uiProcess = null;
            }
        }

        void AppendServerLog(ref string dst, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (serverLogLock)
            {
                dst ??= string.Empty;
                dst += line + "\n";
                const int MaxChars = 8192;
                if (dst.Length > MaxChars)
                {
                    dst = dst.Substring(dst.Length - MaxChars);
                }
            }
        }

        void AppendUiLog(ref string dst, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (uiLogLock)
            {
                dst ??= string.Empty;
                dst += line + "\n";
                const int MaxChars = 8192;
                if (dst.Length > MaxChars)
                {
                    dst = dst.Substring(dst.Length - MaxChars);
                }
            }
        }

        static string SafeTrimServerLog(string s)
        {
            try { return string.IsNullOrWhiteSpace(s) ? null : s.Trim(); }
            catch { return null; }
        }

        static bool TryFindCodelyExecutable(out string path, out string error)
        {
            path = null;
            error = null;

            try
            {
                var env1 = Environment.GetEnvironmentVariable(CodelyExeEnv2);
                if (!string.IsNullOrWhiteSpace(env1) && File.Exists(env1))
                {
                    path = env1;
                    return true;
                }

                var env2 = Environment.GetEnvironmentVariable(CodelyExeEnv);
                if (!string.IsNullOrWhiteSpace(env2) && File.Exists(env2))
                {
                    path = env2;
                    return true;
                }

#if UNITY_EDITOR_OSX
                string[] candidates = { "/opt/homebrew/bin/codely", "/usr/local/bin/codely" };
                foreach (var c in candidates)
                {
                    if (File.Exists(c))
                    {
                        path = c;
                        return true;
                    }
                }
#endif

                // Best-effort fallback: `which codely`
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/which",
                        Arguments = "codely",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                    };
                    using var p = Process.Start(psi);
                    if (p != null && p.WaitForExit(300))
                    {
                        var s = (p.StandardOutput?.ReadToEnd() ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(s) && File.Exists(s))
                        {
                            path = s;
                            return true;
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
            catch
            {
                // ignore
            }

            error =
                "Failed to locate `codely` executable. On macOS, Unity often lacks Homebrew PATH.\n" +
                "Fix options:\n" +
                "- Install codely under /opt/homebrew/bin/codely, or\n" +
                "- Set env UNITY_AGENT_CLIENT_UI_CODELY_EXE=/absolute/path/to/codely, or\n" +
                "- Set env CODELY_EXE=/absolute/path/to/codely.";
            return false;
        }

        void ForceRestartServer()
        {
            try
            {
                StopAll(forceKill: true);

                var pids = FindListeningPids(DefaultPort);
                if (pids.Length > 0)
                {
                    foreach (var pid in pids)
                    {
                        if (pid == Process.GetCurrentProcess().Id) continue;
                        KillProcessTree(pid);
                    }
                    UnityEngine.Debug.Log($"[IPC Sync] force restart: killed port {DefaultPort} listeners: {string.Join(",", pids)}");
                }

                StartIfNeeded(forceRestart: true);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[IPC Sync] force restart failed: {ex.Message}");
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

                // Domain reloads can cause Unity to lose the Process handle while the external tauri-ui process is still alive.
                // Never kill it in that case; prefer attaching to an existing instance to avoid a full restart / web refresh.
                if (TryAttachExistingTauriProcess(tauriExe, out var existing))
                {
                    uiProcess = existing;
                    if (debugLogs && IsProcessAlive(uiProcess))
                    {
                        UnityEngine.Debug.Log($"[IPC Sync] attached existing tauri: exe={tauriExe} pid={uiProcess.Id} ipc={ipcPath}");
                    }
                    return;
                }

                // Reset log tails for the new attempt so we don't mix runs.
                lock (uiLogLock)
                {
                    uiStdoutTail = null;
                    uiStderrTail = null;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = tauriExe,
                    WorkingDirectory = Path.GetDirectoryName(tauriExe) ?? ".",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.EnvironmentVariables["UNITY_AGENT_CLIENT_UI_URL"] = DefaultUrl;
                psi.EnvironmentVariables[TauriIpcPathEnv] = ipcPath;
                psi.EnvironmentVariables[TauriIpcNameEnv] = ipcPath; // back-compat
                psi.EnvironmentVariables[TauriIpcModeEnv] = "sync";
                psi.EnvironmentVariables[TauriIpcDebugEnv] = debugLogs ? "1" : "0";

                uiProcess = Process.Start(psi);
                if (uiProcess != null)
                {
                    try
                    {
                        uiProcess.EnableRaisingEvents = true;
                        uiProcess.OutputDataReceived += (_, e) => AppendUiLog(ref uiStdoutTail, e?.Data);
                        uiProcess.ErrorDataReceived += (_, e) => AppendUiLog(ref uiStderrTail, e?.Data);
                        uiProcess.BeginOutputReadLine();
                        uiProcess.BeginErrorReadLine();
                    }
                    catch
                    {
                        // ignore
                    }

                    // If it dies immediately, surface a helpful error.
                    try
                    {
                        if (uiProcess.WaitForExit(250))
                        {
                            var outTail = SafeTrimServerLog(uiStdoutTail);
                            var errTail = SafeTrimServerLog(uiStderrTail);
                            lastError =
                                $"Tauri exited immediately (code={uiProcess.ExitCode}).\n" +
                                $"exe={tauriExe}\n" +
                                $"cwd={psi.WorkingDirectory}\n" +
                                (!string.IsNullOrWhiteSpace(errTail) ? $"stderr:\n{errTail}\n" : "") +
                                (!string.IsNullOrWhiteSpace(outTail) ? $"stdout:\n{outTail}\n" : "");
                            if (debugLogs)
                            {
                                UnityEngine.Debug.LogError("[IPC Sync] " + lastError);
                            }
                            try { uiProcess.Dispose(); } catch { }
                            uiProcess = null;
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

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

        static bool TryAttachExistingTauriProcess(string tauriExePath, out Process p)
        {
            p = null;
            if (string.IsNullOrWhiteSpace(tauriExePath)) return false;
            try
            {
                Process first = null;
                foreach (var proc in Process.GetProcessesByName("tauri-ui"))
                {
                    if (proc == null) continue;
                    if (first == null) first = proc;

                    string path = null;
                    try { path = proc.MainModule?.FileName; } catch { }
                    if (!string.IsNullOrWhiteSpace(path) && string.Equals(path, tauriExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        p = proc;
                        return !p.HasExited;
                    }
                }

                // Best-effort fallback: if we can't resolve MainModule paths (permission) but a tauri-ui exists,
                // prefer attaching over spawning a duplicate instance (which causes flicker / refresh loops).
                if (first != null)
                {
                    p = first;
                    return !p.HasExited;
                }
            }
            catch
            {
                // ignore
            }

            p = null;
            return false;
        }

        static void TryKillStaleTauriProcesses(string tauriExePath)
        {
            if (string.IsNullOrWhiteSpace(tauriExePath)) return;
            try
            {
                foreach (var p in Process.GetProcessesByName("tauri-ui"))
                {
                    try
                    {
                        string path = null;
                        try { path = p.MainModule?.FileName; } catch { }
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        if (!string.Equals(path, tauriExePath, StringComparison.OrdinalIgnoreCase)) continue;

                        KillProcessTree(p.Id);
                    }
                    catch
                    {
                        // ignore per-process failures
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        void StopAll(bool forceKill)
        {
            try { syncIpc?.WriteFlags(visible: false, active: false); } catch { }
            try { syncIpc?.Dispose(); } catch { }
            syncIpc = null;

            // Stop native plugin
#if UNITY_EDITOR_WIN
            if (nativePluginRunning)
            {
                CodelyWindowSyncNative.TryStop();
                nativePluginRunning = false;
                if (debugLogs)
                {
                    UnityEngine.Debug.Log($"[IPC Sync] native plugin stopped");
                }
            }
#endif

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

        bool IsWebUiPortOpenCached(double now)
        {
            if (now < nextServerProbeAt) return lastServerPortOpen;
            lastServerPortOpen = IsPortOpen("127.0.0.1", DefaultPort, 50);
            nextServerProbeAt = now + 0.5; // avoid blocking per-frame
            return lastServerPortOpen;
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

#if UNITY_EDITOR_OSX
        bool ShouldPublishRectMac(int x, int y, int w, int h, bool visible, bool active, long ownerHwnd)
        {
            // If we've never published a rect yet, always publish.
            if (!hasLastPublishedRect) return true;

            // Only publish when something materially changed.
            if (x != lastPublishedX || y != lastPublishedY || w != lastPublishedW || h != lastPublishedH) return true;
            if (visible != lastPublishedVisible || active != lastPublishedActive) return true;
            if (ownerHwnd != lastPublishedOwnerHwnd) return true;
            return false;
        }

        void CacheLastPublishedRectMac(int x, int y, int w, int h, bool visible, bool active, long ownerHwnd)
        {
            hasLastPublishedRect = true;
            lastPublishedX = x;
            lastPublishedY = y;
            lastPublishedW = w;
            lastPublishedH = h;
            lastPublishedVisible = visible;
            lastPublishedActive = active;
            lastPublishedOwnerHwnd = ownerHwnd;
        }
#endif

        void TrySignalDetachToTauri()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ipcPath)) return;

                // Sidecar flag file: simple cross-process signalling without extra IPC.
                // - when present and contains "1", tauri stops following Unity and shows decorations.
                // - when absent or contains "0", tauri follows IPC sync.
                File.WriteAllText(ipcPath + ".detach", "1", Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }

        void TrySignalAttachToTauri()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ipcPath)) return;
                File.WriteAllText(ipcPath + ".detach", "0", Encoding.UTF8);
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
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

#if UNITY_EDITOR_WIN
                psi.FileName = "taskkill";
                psi.Arguments = $"/PID {pid} /T /F";
#else
                // Best-effort: kill children then parent.
                // We avoid interactive prompts and ignore failures (process may already be dead).
                psi.FileName = "/bin/bash";
                psi.Arguments =
                    $"-lc \"(pkill -TERM -P {pid} >/dev/null 2>&1 || true); (kill -TERM {pid} >/dev/null 2>&1 || true); " +
                    $"sleep 0.2; (pkill -KILL -P {pid} >/dev/null 2>&1 || true); (kill -KILL {pid} >/dev/null 2>&1 || true)\"";
#endif

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
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
#if UNITY_EDITOR_WIN
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c netstat -ano -p tcp | findstr LISTENING | findstr :{port}";
#else
                psi.FileName = "/bin/bash";
                psi.Arguments = $"-lc \"lsof -nP -iTCP:{port} -sTCP:LISTEN -t 2>/dev/null\"";
#endif

                using var p = Process.Start(psi);
                var output = p?.StandardOutput?.ReadToEnd() ?? "";
                p?.WaitForExit(800);

#if UNITY_EDITOR_WIN
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
#else
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return Array.Empty<int>();
                var list = new System.Collections.Generic.List<int>(lines.Length);
                foreach (var line in lines)
                {
                    if (int.TryParse(line.Trim(), out var pid) && pid > 0 && !list.Contains(pid))
                    {
                        list.Add(pid);
                    }
                }
                return list.ToArray();
#endif
            }
            catch
            {
                return Array.Empty<int>();
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

        void DumpState()
        {
            try
            {
                var now = EditorApplication.timeSinceStartup;
                var unityFocusedRaw = EditorWindow.focusedWindow == this || hasFocus;
                var unityFocused = unityFocusedRaw || (now - lastFocusedAt) < 0.75;
                var unityAppActive = IsUnityApplicationActive();
                var fgPid = 0;
                var unityPid = Process.GetCurrentProcess().Id;
                var tauriPid = IsProcessAlive(uiProcess) ? uiProcess.Id : 0;
                var tauriForeground = false;
#if UNITY_EDITOR_WIN
                fgPid = GetForegroundPid();
                tauriForeground = tauriPid != 0 && fgPid == tauriPid;
#endif
                var unityForeground = fgPid != 0 && fgPid == unityPid;
                var forceShowLeft = forceShowUntil - now;

                var unityActive = unityAppActive || unityForeground;
                var visibleWanted = !userHidden && (tauriForeground || (unityActive && unityFocused));
                var activeWanted = visibleWanted;

                UnityEngine.Debug.Log(
                    $"[IPC Sync] state: now={now:0.000} userHidden={userHidden} " +
                    $"unityFocusedRaw={unityFocusedRaw} unityFocusedSmoothed={unityFocused} focusedWindow={EditorWindow.focusedWindow?.GetType().Name ?? "null"} " +
                    $"unityAppActive={unityAppActive} unityFg={unityForeground} tauriFg={tauriForeground} fgPid={fgPid} unityPid={unityPid} tauriPid={tauriPid} " +
                    $"unityActive={unityActive} visibleWanted={visibleWanted} activeWanted={activeWanted} forceShowLeft={forceShowLeft:0.000}s " +
                    $"ipc={ipcPath} seq={(syncIpc != null ? syncIpc.LastSeq : 0)}");

                DumpIpcSnapshot();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[IPC Sync] state dump failed: " + ex.Message);
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


