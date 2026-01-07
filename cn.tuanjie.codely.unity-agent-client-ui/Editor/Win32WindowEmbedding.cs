using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Codely.UnityAgentClientUI
{
    /// <summary>
    /// Minimal Win32 interop to embed an external process window inside a Unity EditorWindow (Windows Editor only).
    /// </summary>
    internal static class Win32WindowEmbedding
    {
#if UNITY_EDITOR_WIN
        const int GWL_STYLE = -16;

        const int WS_CHILD = 0x40000000;
        const int WS_CAPTION = 0x00C00000;
        const int WS_THICKFRAME = 0x00040000;
        const int WS_MINIMIZEBOX = 0x00020000;
        const int WS_MAXIMIZEBOX = 0x00010000;
        const int WS_SYSMENU = 0x00080000;
        const int WS_BORDER = 0x00800000;
        const int WS_POPUP = unchecked((int)0x80000000);

        const int SW_SHOW = 5;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll")]
        static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        public static bool TryFindTopLevelWindowForProcess(int pid, out IntPtr hwnd)
        {
            hwnd = IntPtr.Zero;
            if (pid <= 0) return false;

            var matches = new List<(IntPtr hwnd, int area, bool hasTitle)>();

            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var winPid);
                if (winPid != (uint)pid) return true;
                if (!IsWindowVisible(h)) return true;

                if (!GetWindowRect(h, out var r)) return true;
                var w = Math.Max(0, r.Right - r.Left);
                var hh = Math.Max(0, r.Bottom - r.Top);
                var area = w * hh;
                if (area <= 64 * 64) return true; // ignore tiny/dummy windows

                // Important: some GUI apps (including WebView2) can have a visible main window
                // with an empty title. We still consider it a valid candidate.
                var hasTitle = GetWindowTextLength(h) > 0;
                matches.Add((h, area, hasTitle));
                return true;
            }, IntPtr.Zero);

            if (matches.Count == 0) return false;

            // Prefer the largest visible window (usually the app's main window).
            // If there are ties, prefer the one that has a title.
            hwnd = matches
                .OrderByDescending(m => m.area)
                .ThenByDescending(m => m.hasTitle)
                .Select(m => m.hwnd)
                .FirstOrDefault();
            return hwnd != IntPtr.Zero;
        }

        public static void EnsureEmbedded(IntPtr childHwnd, IntPtr parentHwnd)
        {
            if (childHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero) return;

            // Ensure child relationship (avoid reparenting every frame; it can cause flicker on WebView windows).
            var currentParent = GetParent(childHwnd);
            if (currentParent != parentHwnd)
            {
                SetParent(childHwnd, parentHwnd);
            }

            // Remove decorations + make it a child window.
            var style = GetWindowLong(childHwnd, GWL_STYLE);
            var desired = style;
            desired &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU | WS_BORDER);
            desired |= WS_CHILD;
            desired &= ~WS_POPUP;
            if (desired != style)
            {
                SetWindowLong(childHwnd, GWL_STYLE, desired);
            }

            ShowWindow(childHwnd, SW_SHOW);
        }

        public static bool IsValidWindowHandle(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

        public static bool TryGetClientRectForScreenRect(IntPtr parentHwnd, Rect targetRectScreenPoints, out int x, out int y, out int w, out int h)
            => TryScreenRectToClientRect(parentHwnd, targetRectScreenPoints, out x, out y, out w, out h);

        public static void SetChildBounds(IntPtr childHwnd, int x, int y, int w, int h, bool repaint = true)
        {
            if (childHwnd == IntPtr.Zero) return;
            MoveWindow(childHwnd, x, y, w, h, bRepaint: repaint);
        }

        public static bool TryGetHwndFromScreenPoint(Vector2 screenPointPoints, out IntPtr hwnd)
        {
            // Unity's GUIToScreenPoint returns coordinates in "points" (not physical pixels) on high-DPI setups.
            // Win32 APIs expect physical pixels. Prefer scaling by pixelsPerPoint.
            var ppp = EditorGUIUtility.pixelsPerPoint;
            hwnd = WindowFromPoint(new POINT
            {
                X = Mathf.RoundToInt(screenPointPoints.x * ppp),
                Y = Mathf.RoundToInt(screenPointPoints.y * ppp),
            });

            if (hwnd != IntPtr.Zero) return true;

            // Fallback: try unscaled (some environments report pixels already).
            hwnd = WindowFromPoint(new POINT
            {
                X = Mathf.RoundToInt(screenPointPoints.x),
                Y = Mathf.RoundToInt(screenPointPoints.y),
            });
            return hwnd != IntPtr.Zero;
        }

        public static bool TryGetUnityGuiViewHwndFromScreenPoint(Vector2 screenPointPoints, out IntPtr hwnd)
        {
            hwnd = IntPtr.Zero;

            if (!TryGetHwndFromScreenPoint(screenPointPoints, out var h) || h == IntPtr.Zero)
            {
                return false;
            }

            // Walk up the parent chain until we find Unity's GUI view window class.
            // Important: after embedding, WindowFromPoint will often return the embedded HWND (or a WebView child).
            // Walking parents keeps the host stable and prevents flicker from re-parenting into itself.
            var cur = h;
            while (cur != IntPtr.Zero)
            {
                if (IsUnityGuiViewHwnd(cur))
                {
                    hwnd = cur;
                    return true;
                }
                cur = GetParent(cur);
            }

            return false;
        }

        static bool IsUnityGuiViewHwnd(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                var sb = new StringBuilder(256);
                var len = GetClassName(hwnd, sb, sb.Capacity);
                if (len <= 0) return false;

                var cls = sb.ToString();
                // Observed on Unity 2022: "UnityGUIViewWndClass"
                return cls.IndexOf("UnityGUIViewWndClass", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        static bool TryScreenRectToClientRect(IntPtr parentHwnd, Rect screenRect, out int x, out int y, out int w, out int h)
        {
            var ppp = EditorGUIUtility.pixelsPerPoint;
            if (ppp > 0.01f && TryScreenRectToClientRect(parentHwnd, screenRect, scale: ppp, out x, out y, out w, out h))
            {
                return true;
            }

            // Fallback: try unscaled (if Unity provided pixels already).
            if (TryScreenRectToClientRect(parentHwnd, screenRect, scale: 1f, out x, out y, out w, out h))
            {
                return true;
            }

            x = y = w = h = 0;
            return false;
        }

        static bool TryScreenRectToClientRect(IntPtr parentHwnd, Rect screenRect, float scale, out int x, out int y, out int w, out int h)
        {
            var tl = new POINT
            {
                X = Mathf.RoundToInt(screenRect.xMin * scale),
                Y = Mathf.RoundToInt(screenRect.yMin * scale),
            };
            var br = new POINT
            {
                X = Mathf.RoundToInt(screenRect.xMax * scale),
                Y = Mathf.RoundToInt(screenRect.yMax * scale),
            };

            if (!ScreenToClient(parentHwnd, ref tl)) { x = y = w = h = 0; return false; }
            if (!ScreenToClient(parentHwnd, ref br)) { x = y = w = h = 0; return false; }

            x = tl.X;
            y = tl.Y;
            w = Math.Max(1, br.X - tl.X);
            h = Math.Max(1, br.Y - tl.Y);
            return true;
        }

        public static bool TryGetEditorWindowHwnd(EditorWindow window, out IntPtr hwnd, out string error)
        {
            hwnd = IntPtr.Zero;
            error = null;
            if (window == null)
            {
                error = "window is null";
                return false;
            }

            try
            {
                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // EditorWindow.m_Parent is a HostView.
                var parentField = typeof(EditorWindow).GetField("m_Parent", Flags);
                var hostView = parentField?.GetValue(window);
                if (hostView == null)
                {
                    error = "EditorWindow.m_Parent is null";
                    return false;
                }

                // HostView.window is a ContainerWindow.
                object containerWindow = null;
                var windowProp = hostView.GetType().GetProperty("window", Flags);
                if (windowProp != null)
                {
                    containerWindow = windowProp.GetValue(hostView, null);
                }
                else
                {
                    var windowField = hostView.GetType().GetField("window", Flags);
                    if (windowField != null)
                    {
                        containerWindow = windowField.GetValue(hostView);
                    }
                }

                if (containerWindow == null)
                {
                    error = "HostView.window is null";
                    return false;
                }

                var cwType = containerWindow.GetType();

                // Most Unity versions use "m_WindowPtr" as a HWND-like value on Windows.
                // Type can vary across Unity versions (IntPtr/long/int), so be defensive.
                var ptrField = cwType.GetField("m_WindowPtr", Flags);
                if (ptrField == null)
                {
                    ptrField = cwType
                        .GetFields(Flags)
                        .FirstOrDefault(f =>
                            f.Name.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            f.Name.IndexOf("Ptr", StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (ptrField == null)
                {
                    error = "ContainerWindow window ptr field not found";
                    return false;
                }

                var raw = ptrField.GetValue(containerWindow);
                if (!TryConvertToIntPtr(raw, out hwnd))
                {
                    error = $"Unsupported window ptr field type: {(raw == null ? "null" : raw.GetType().FullName)}";
                    return false;
                }
                if (hwnd == IntPtr.Zero)
                {
                    error = "HWND is zero";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static bool TryConvertToIntPtr(object value, out IntPtr ptr)
        {
            ptr = IntPtr.Zero;
            if (value == null) return false;

            switch (value)
            {
                case IntPtr p:
                    ptr = p;
                    return true;
                case long l:
                    ptr = new IntPtr(l);
                    return true;
                case int i:
                    ptr = new IntPtr(i);
                    return true;
                case uint u:
                    ptr = new IntPtr(unchecked((int)u));
                    return true;
            }

            // Unity 2022+ uses UnityEditor.MonoReloadableIntPtr for some internal HWND fields.
            // We can't reference the type directly (it's internal), so we unwrap via reflection.
            try
            {
                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var t = value.GetType();

                // Try common "ToInt64" pattern.
                var toInt64 = t.GetMethod("ToInt64", Flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                if (toInt64 != null && toInt64.ReturnType == typeof(long))
                {
                    var l = (long)toInt64.Invoke(value, null);
                    ptr = new IntPtr(l);
                    return true;
                }

                // Try common "Value" property pattern.
                var valueProp = t.GetProperty("Value", Flags) ?? t.GetProperty("value", Flags);
                if (valueProp != null)
                {
                    var inner = valueProp.GetValue(value, null);
                    if (TryConvertToIntPtr(inner, out ptr)) return true;
                }

                // Try common backing field names.
                var field = t.GetField("m_IntPtr", Flags) ?? t.GetField("m_Value", Flags) ?? t.GetFields(Flags).FirstOrDefault(f => f.FieldType == typeof(IntPtr));
                if (field != null)
                {
                    var inner = field.GetValue(value);
                    if (TryConvertToIntPtr(inner, out ptr)) return true;
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }
#else
        public static bool TryFindTopLevelWindowForProcess(int pid, out IntPtr hwnd)
        {
            hwnd = IntPtr.Zero;
            return false;
        }

        public static void EmbedAndMove(IntPtr childHwnd, IntPtr parentHwnd, Rect targetRectScreenPoints) { }

        public static bool TryGetEditorWindowHwnd(EditorWindow window, out IntPtr hwnd, out string error)
        {
            hwnd = IntPtr.Zero;
            error = "Win32 embedding is only supported on Windows Editor";
            return false;
        }
#endif
    }
}


