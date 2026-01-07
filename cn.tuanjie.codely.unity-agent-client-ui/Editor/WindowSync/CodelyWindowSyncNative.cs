using System;
using System.Runtime.InteropServices;

namespace Codely.UnityAgentClientUI
{
    /// <summary>
    /// Optional Windows native plugin to publish host rect at high frequency even while Unity's UI thread is blocked (e.g. docking/dragging).
    /// If the DLL isn't present, we fall back to C# MemoryMappedFile writes.
    /// </summary>
    internal static class CodelyWindowSyncNative
    {
#if UNITY_EDITOR_WIN
        const string DllName = "CodelyUnityAgentClientUIWindowSync";

        [DllImport(DllName, CharSet = CharSet.Unicode, EntryPoint = "codely_ws_start", CallingConvention = CallingConvention.Cdecl)]
        static extern int StartInternal(long hostHwnd, string mappingName);

        [DllImport(DllName, EntryPoint = "codely_ws_stop", CallingConvention = CallingConvention.Cdecl)]
        static extern void StopInternal();
#endif

        public static bool TryStart(long hostHwnd, string mappingName)
        {
#if UNITY_EDITOR_WIN
            try
            {
                var rc = StartInternal(hostHwnd, mappingName);
                return rc == 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }

        public static void TryStop()
        {
#if UNITY_EDITOR_WIN
            try { StopInternal(); } catch { }
#endif
        }
    }
}


