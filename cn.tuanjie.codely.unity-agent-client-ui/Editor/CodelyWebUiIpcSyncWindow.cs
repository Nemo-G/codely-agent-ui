using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
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
        const string SessionKey_UserHidden = "Codely.UnityAgentClientUI.IpcSync.UserHidden";

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

        bool userHidden;
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

        static bool s_checkedUnityAppActive;
        static PropertyInfo s_unityIsAppActiveProp;

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
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += PersistForDomainReload;
            EditorApplication.quitting += HandleQuit;

            TryRestoreFromDomainReload();
            try { userHidden = SessionState.GetBool(SessionKey_UserHidden, false); } catch { userHidden = false; }
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
            try
            {
                if (IsProcessAlive(serveProcess)) SessionState.SetInt(SessionKey_ServerPid, serveProcess.Id);
                if (IsProcessAlive(uiProcess)) SessionState.SetInt(SessionKey_UiPid, uiProcess.Id);
                if (!string.IsNullOrWhiteSpace(ipcPath)) SessionState.SetString(SessionKey_IpcPath, ipcPath);
                SessionState.SetBool(SessionKey_UserHidden, userHidden);
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
            return false;
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
            if (debugLogs)
            {
                UnityEngine.Debug.Log($"[IPC Sync] RequestShow: now={now:0.000} forceShowFor={forceShowUntil - now:0.000}s ipc={ipcPath}");
            }

            // Clear potentially-poisoned rect cache (e.g. bogus huge negative coords during move/resize).
            hasLastGoodRect = false;
            wroteRectOnce = false;

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
                    syncIpc?.WriteRect(xPx, yPx, wPx, hPx, visible: true, active: true, ownerHwnd: ownerHwnd);
                }
                else
                {
                    // Last-resort: recenter to virtual screen so the user can recover even if the current rect is missing/garbage.
                    if (TryGetFallbackRectPx(out var fx, out var fy, out var fw, out var fh))
                    {
                        hasLastGoodRect = true;
                        lastGoodX = fx; lastGoodY = fy; lastGoodW = fw; lastGoodH = fh;
                        syncIpc?.WriteRect(fx, fy, fw, fh, visible: true, active: true, ownerHwnd: ownerHwnd);
                    }
                    else
                    {
                        syncIpc?.WriteFlags(visible: true, active: true);
                    }
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

                if (GUI.Button(r, "Show Agent Window"))
                {
                    RequestShow();
                }
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

                // If we lost focus to another Unity tab/window, hide immediately (unless the user clicked into tauri-ui).
#if UNITY_EDITOR_WIN
                if (pendingAutoHide)
                {
                    var now0 = EditorApplication.timeSinceStartup;
                    var fgPid0 = GetForegroundPid();
                    var unityPid0 = Process.GetCurrentProcess().Id;
                    var tauriPid0 = IsProcessAlive(uiProcess) ? uiProcess.Id : 0;
                    var tauriForeground0 = tauriPid0 != 0 && fgPid0 == tauriPid0;

                    // Cancel auto-hide if the user is actually interacting with tauri-ui.
                    if (tauriForeground0)
                    {
                        pendingAutoHide = false;
                    }
                    else if ((now0 - pendingAutoHideAt) >= 0.016) // ~1 frame grace
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
#else
                if (pendingAutoHide)
                {
                    pendingAutoHide = false;
                    try { syncIpc?.WriteFlags(visible: false, active: false); } catch { }
                    wroteRectOnce = false;
                    lastDesiredVisible = false;
                    lastDesiredActive = false;
                    return;
                }
#endif

                // Keep server alive (only start if port isn't already served).
                if (!IsProcessAlive(serveProcess) && !IsPortOpen("127.0.0.1", DefaultPort, 50))
                {
                    StartServer();
                }

                // Launch Tauri if not running (unless user explicitly hid it).
                if (!userHidden && !IsProcessAlive(uiProcess))
                {
                    StartTauri();
                }

                var rectOk = embedRectScreenPoints.width > 1f && embedRectScreenPoints.height > 1f;

                // Visibility / layering policy (per user request):
                // - When Unity (or tauri-ui) is the active foreground app => tauri window may be visible.
                // - When THIS EditorWindow is focused (or during "Show" rescue window) => tauri window must be on top.
                // - When Unity is not foreground (other app active) => tauri must hide (no global always-on-top).
                var now = EditorApplication.timeSinceStartup;

                var unityFocusedRaw = EditorWindow.focusedWindow == this || hasFocus;
                if (unityFocusedRaw) lastFocusedAt = now;
                // Focus can jitter during dock/move/resize; smooth it slightly.
                var unityFocused = unityFocusedRaw || (now - lastFocusedAt) < 0.2;

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
                var unityActive = unityAppActive || unityForeground;

                // Requested behavior:
                // - When switching to other Unity windows/tabs, hide.
                // - When this agent window is focused, show.
                // - If the user clicks into tauri-ui, keep it visible so it can be interacted with.
                // - If Unity isn't active and tauri-ui isn't foreground, hide (never cover other apps).
                var visibleWanted = !userHidden && (tauriForeground || (unityActive && unityFocused));
                var activeWanted = visibleWanted; // when visible, keep it topmost (tauri will translate active=>HWND_TOPMOST)

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
                    try { syncIpc?.WriteFlags(visible: false, active: false); } catch { }
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
                        syncIpc.WriteRect(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
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
                    // We have no valid rect yet. Publish a fallback on-screen rect so Tauri can actually show (no-flash mode requires w/h>0).
                    if (TryGetFallbackRectPx(out var fx, out var fy, out var fw, out var fh))
                    {
                        hasLastGoodRect = true;
                        lastGoodX = fx; lastGoodY = fy; lastGoodW = fw; lastGoodH = fh;
                        syncIpc.WriteRect(fx, fy, fw, fh, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
                        wroteRectOnce = true;
                        lastError = null;

                        if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                        {
                            nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                            UnityEngine.Debug.Log($"[IPC Sync] rect not ready; fallback recenter: seq={syncIpc.LastSeq} rect=({fx},{fy},{fw},{fh}) visible=1 active={(activeWanted ? 1 : 0)} owner=0x{ownerHwnd:X} unityAppActive={unityAppActive} unityFg={unityForeground} unityFocused={unityFocused} tauriFg={tauriForeground} fgPid={fgPid} ipc={ipcPath}");
                        }
                        return;
                    }

                    // No fallback available; keep visible state, but don't move.
                    try { syncIpc?.WriteFlags(visible: true, active: activeWanted); } catch { }
                    wroteRectOnce = false;
                    if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                    {
                        nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                        UnityEngine.Debug.Log($"[IPC Sync] rect not ready yet: visible=1 active={(activeWanted ? 1 : 0)} unityAppActive={unityAppActive} unityFg={unityForeground} unityFocused={unityFocused} tauriFg={tauriForeground} fgPid={fgPid} rect=({embedRectScreenPoints.xMin:0.##},{embedRectScreenPoints.yMin:0.##},{embedRectScreenPoints.width:0.##},{embedRectScreenPoints.height:0.##}) ppp={EditorGUIUtility.pixelsPerPoint:0.##} ipc={ipcPath} seq={(syncIpc != null ? syncIpc.LastSeq : 0)}");
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
                        syncIpc.WriteRect(lastGoodX, lastGoodY, lastGoodW, lastGoodH, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
                        wroteRectOnce = true;
                        lastError = null;
                        if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                        {
                            nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                            UnityEngine.Debug.Log($"[IPC Sync] rect insane; using lastGood rect: seq={syncIpc.LastSeq} rect=({lastGoodX},{lastGoodY},{lastGoodW},{lastGoodH}) visible=1 active={(activeWanted ? 1 : 0)} owner=0x{ownerHwnd:X} unityAppActive={unityAppActive} unityFg={unityForeground} unityFocused={unityFocused} tauriFg={tauriForeground} fgPid={fgPid} ipc={ipcPath}");
                        }
                        return;
                    }

                    // No sane rect to publish yet.
                    // If we don't have ANY known-good rect, publish a fallback on-screen rect so the user can recover.
                    if (!hasLastGoodRect && TryGetFallbackRectPx(out var fx, out var fy, out var fw, out var fh))
                    {
                        hasLastGoodRect = true;
                        lastGoodX = fx; lastGoodY = fy; lastGoodW = fw; lastGoodH = fh;
                        syncIpc.WriteRect(fx, fy, fw, fh, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
                        wroteRectOnce = true;
                        lastError = null;

                        if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                        {
                            nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                            UnityEngine.Debug.Log($"[IPC Sync] rect insane; fallback recenter: seq={syncIpc.LastSeq} rect=({fx},{fy},{fw},{fh}) visible=1 active={(activeWanted ? 1 : 0)} owner=0x{ownerHwnd:X} unityAppActive={unityAppActive} unityFg={unityForeground} unityFocused={unityFocused} tauriFg={tauriForeground} fgPid={fgPid} ipc={ipcPath}");
                        }
                        return;
                    }

                    // Otherwise: keep visible state, but don't move (avoid moving off-screen).
                    try { syncIpc?.WriteFlags(visible: true, active: activeWanted); } catch { }
                    wroteRectOnce = false;
                    if (debugLogs && EditorApplication.timeSinceStartup >= nextDebugLogAt)
                    {
                        nextDebugLogAt = EditorApplication.timeSinceStartup + 1.0;
                        UnityEngine.Debug.Log($"[IPC Sync] rect insane; skip publish: visible=1 active={(activeWanted ? 1 : 0)} unityAppActive={unityAppActive} unityFg={unityForeground} unityFocused={unityFocused} tauriFg={tauriForeground} fgPid={fgPid} rect=({embedRectScreenPoints.xMin:0.##},{embedRectScreenPoints.yMin:0.##},{embedRectScreenPoints.width:0.##},{embedRectScreenPoints.height:0.##}) ppp={EditorGUIUtility.pixelsPerPoint:0.##} usedScale={usedScale:0.##} ipc={ipcPath} seq={(syncIpc != null ? syncIpc.LastSeq : 0)}");
                    }
                    return;
                }

                hasLastGoodRect = true;
                lastGoodX = xPx; lastGoodY = yPx; lastGoodW = wPx; lastGoodH = hPx;

                syncIpc.WriteRect(xPx, yPx, wPx, hPx, visible: true, active: activeWanted, ownerHwnd: ownerHwnd);
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

            if (!IsProcessAlive(serveProcess) && !IsPortOpen("127.0.0.1", DefaultPort, 50))
            {
                StartServer();
            }

            if (!userHidden && !IsProcessAlive(uiProcess))
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

                // Ensure we don't end up with multiple tauri-ui instances fighting each other (causes visible flicker).
                // This can happen after domain reloads or if Unity lost track of the prior PID.
                TryKillStaleTauriProcesses(tauriExe);

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


