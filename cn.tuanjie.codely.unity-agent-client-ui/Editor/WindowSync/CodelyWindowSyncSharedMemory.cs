using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Codely.UnityAgentClientUI
{
    /// <summary>
    /// Windows-only: shared memory for syncing a top-level Tauri window to a Unity EditorWindow rect without SetParent.
    /// </summary>
    internal sealed class CodelyWindowSyncSharedMemory : IDisposable
    {
        // Layout (bytes):
        // 0  u32 magic = 'CUI1'
        // 4  u32 version = 1
        // 8  u32 seq (increment each write)
        // 12 i32 x
        // 16 i32 y
        // 20 i32 w
        // 24 i32 h
        // 28 i32 reserved (toolbar px for future / padding)
        // 32 u32 flags (bit0: visible, bit1: active)
        const int SizeBytes = 64;
        const uint Magic = 0x31495543; // "CUI1" little-endian
        const uint Version = 1;
        const uint FlagVisible = 1u << 0;
        const uint FlagActive = 1u << 1;

        readonly string path;
        readonly FileStream file;
        readonly MemoryMappedFile mmf;
        readonly MemoryMappedViewAccessor accessor;
        uint seq;

        CodelyWindowSyncSharedMemory(string path, FileStream file, MemoryMappedFile mmf, MemoryMappedViewAccessor accessor)
        {
            this.path = path;
            this.file = file;
            this.mmf = mmf;
            this.accessor = accessor;
        }

        public static string DefaultMappingPath()
        {
            // Stable per-Unity-process name; keeps Tauri and Unity in sync across domain reloads.
            var pid = Process.GetCurrentProcess().Id;
            return System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodelyUnityAgentClientUI_{pid}.mmf");
        }

        public static CodelyWindowSyncSharedMemory OpenOrCreate(string mappingPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mappingPath) ?? ".");

            // File-backed mapping: cross-platform and easy for the Tauri process to open and mmap.
            var file = new FileStream(mappingPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (file.Length < SizeBytes) file.SetLength(SizeBytes);

            var mmf = MemoryMappedFile.CreateFromFile(file, mapName: null, capacity: SizeBytes, access: MemoryMappedFileAccess.ReadWrite, inheritability: HandleInheritability.None, leaveOpen: true);
            var accessor = mmf.CreateViewAccessor(0, SizeBytes, MemoryMappedFileAccess.ReadWrite);

            var ipc = new CodelyWindowSyncSharedMemory(mappingPath, file, mmf, accessor);
            ipc.InitializeHeaderIfNeeded();
            return ipc;
        }

        void InitializeHeaderIfNeeded()
        {
            try
            {
                accessor.Read(0, out uint magic);
                accessor.Read(4, out uint version);
                if (magic == Magic && version == Version) return;

                accessor.Write(0, Magic);
                accessor.Write(4, Version);
                accessor.Write(8, 0u);
                accessor.Write(12, 0);
                accessor.Write(16, 0);
                accessor.Write(20, 0);
                accessor.Write(24, 0);
                accessor.Write(28, 0);
                accessor.Write(32, 0u);
                accessor.Write(40, 0L);
                accessor.Flush();
            }
            catch
            {
                // ignore
            }
        }

        public string MappingPath => path;
        public uint LastSeq => seq;

        public void WriteRect(int x, int y, int w, int h, bool visible, bool active, long ownerHwnd = 0)
        {
            // NOTE: Keep this as cheap as possible; called every editor tick.
            // Two-phase commit: write data first, then update seq last so readers can detect torn writes.
            try
            {
                accessor.Write(12, x);
                accessor.Write(16, y);
                accessor.Write(20, w);
                accessor.Write(24, h);
                var flags = (visible ? FlagVisible : 0u) | (active ? FlagActive : 0u);
                accessor.Write(32, flags);
                accessor.Write(40, ownerHwnd);

                seq++;
                accessor.Write(8, seq);

                // Avoid flushing each tick; OS will keep it in RAM. Readers just map the same pages.
            }
            catch
            {
                // ignore
            }
        }

        public void WriteFlags(bool visible, bool active)
        {
            try
            {
                var flags = (visible ? FlagVisible : 0u) | (active ? FlagActive : 0u);
                accessor.Write(32, flags);
                seq++;
                accessor.Write(8, seq);
            }
            catch
            {
                // ignore
            }
        }

        public void Dispose()
        {
            try { accessor?.Dispose(); } catch { }
            try { mmf?.Dispose(); } catch { }
            try { file?.Dispose(); } catch { }
        }
    }
}


