using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UnityAgentClient
{
    internal static class Win32HandleUtil
    {
#if UNITY_EDITOR_WIN
        const uint DUPLICATE_SAME_ACCESS = 0x00000002;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DuplicateHandle(
            IntPtr hSourceProcessHandle,
            IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle,
            out IntPtr lpTargetHandle,
            uint dwDesiredAccess,
            bool bInheritHandle,
            uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        public static bool TryDuplicateStreamHandle(Stream stream, out long duplicatedHandle)
        {
            return TryDuplicateStreamHandle(stream, out duplicatedHandle, out _);
        }

        public static bool TryDuplicateStreamHandle(Stream stream, out long duplicatedHandle, out int lastWin32Error)
        {
            duplicatedHandle = 0;
            lastWin32Error = 0;
            if (stream == null) return false;

            var safe = TryGetSafeHandle(stream);
            if (safe == null || safe.IsInvalid) return false;

            var source = safe.DangerousGetHandle();
            var proc = GetCurrentProcess();
            if (!DuplicateHandle(proc, source, proc, out var dup, 0, false, DUPLICATE_SAME_ACCESS))
            {
                lastWin32Error = Marshal.GetLastWin32Error();
                return false;
            }

            duplicatedHandle = dup.ToInt64();
            return true;
        }

        public static bool TryCreateReaderFromHandle(long handleValue, out StreamReader reader)
        {
            reader = null;
            if (handleValue == 0) return false;

            try
            {
                var handle = new IntPtr(handleValue);
                var safe = new SafeFileHandle(handle, ownsHandle: true);
                var fs = new FileStream(safe, FileAccess.Read, bufferSize: 4096, isAsync: true);
                reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
                return true;
            }
            catch
            {
                // If we created a SafeFileHandle with ownsHandle:true and then failed, it might already be closed.
                return false;
            }
        }

        public static bool TryCreateWriterFromHandle(long handleValue, out StreamWriter writer)
        {
            writer = null;
            if (handleValue == 0) return false;

            try
            {
                var handle = new IntPtr(handleValue);
                var safe = new SafeFileHandle(handle, ownsHandle: true);
                var fs = new FileStream(safe, FileAccess.Write, bufferSize: 4096, isAsync: true);
                writer = new StreamWriter(fs, System.Text.Encoding.UTF8, bufferSize: 4096) { AutoFlush = true, NewLine = "\n" };
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void CloseHandleIfValid(long handleValue)
        {
            if (handleValue == 0) return;
            try
            {
                CloseHandle(new IntPtr(handleValue));
            }
            catch
            {
                // ignore
            }
        }

        static SafeHandle TryGetSafeHandle(Stream stream)
        {
            switch (stream)
            {
                case FileStream fs:
                    return fs.SafeFileHandle;
                case PipeStream ps:
                    return ps.SafePipeHandle;
                default:
                    return null;
            }
        }
#else
        public static bool TryDuplicateStreamHandle(Stream stream, out long duplicatedHandle)
        {
            duplicatedHandle = 0;
            return false;
        }

        public static bool TryDuplicateStreamHandle(Stream stream, out long duplicatedHandle, out int lastWin32Error)
        {
            duplicatedHandle = 0;
            lastWin32Error = 0;
            return false;
        }

        public static bool TryCreateReaderFromHandle(long handleValue, out StreamReader reader)
        {
            reader = null;
            return false;
        }

        public static bool TryCreateWriterFromHandle(long handleValue, out StreamWriter writer)
        {
            writer = null;
            return false;
        }

        public static void CloseHandleIfValid(long handleValue) { }
#endif
    }
}


