using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using AgentClientProtocol;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityAgentClient
{
    /// <summary>
    /// Persists the last ACP session id and UI history across Unity domain reloads.
    /// This does NOT attempt to persist the live JSON-RPC connection; it enables reconnect + session/load.
    /// </summary>
    internal static class AgentSessionPersistence
    {
        const int CurrentVersion = 2;

        [Serializable]
        sealed class AgentSessionStateFile
        {
            public int Version = CurrentVersion;
            public string SessionId;
            public string MessagesJson;
            public string LastUpdatedUtc;

            // Live process/stdio handoff for domain reload (Windows-only; valid only within the same Unity process).
            public int UnityPid;
            public int AgentPid;
            public long AgentStdinHandle;
            public long AgentStdoutHandle;
            public long AgentStderrHandle;
            public int JsonRpcRequestIdSeed;
            public bool SessionLoadUnsupported;
        }

        static readonly object fileLock = new();
        static AgentSessionStateFile cached;

        public static string GetSessionId()
        {
            return LoadState().SessionId;
        }

        public static bool IsSessionLoadUnsupported()
        {
            return LoadState().SessionLoadUnsupported;
        }

        public static void MarkSessionLoadUnsupported()
        {
            var s = LoadState();
            s.SessionLoadUnsupported = true;
            s.LastUpdatedUtc = DateTime.UtcNow.ToString("o");
            SaveState(s);
        }

        public static void SetSessionId(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            var s = LoadState();
            s.Version = CurrentVersion;
            s.SessionId = sessionId;
            s.LastUpdatedUtc = DateTime.UtcNow.ToString("o");
            SaveState(s);
        }

        public static void SaveLiveTransport(int agentPid, long stdinHandle, long stdoutHandle, long stderrHandle, int requestIdSeed)
        {
            var s = LoadState();
            s.UnityPid = Process.GetCurrentProcess().Id;
            s.AgentPid = agentPid;
            s.AgentStdinHandle = stdinHandle;
            s.AgentStdoutHandle = stdoutHandle;
            s.AgentStderrHandle = stderrHandle;
            s.JsonRpcRequestIdSeed = requestIdSeed;
            s.LastUpdatedUtc = DateTime.UtcNow.ToString("o");
            SaveState(s);
        }

        public static bool TryGetLiveTransport(out int agentPid, out long stdinHandle, out long stdoutHandle, out long stderrHandle, out int requestIdSeed)
        {
            agentPid = 0;
            stdinHandle = 0;
            stdoutHandle = 0;
            stderrHandle = 0;
            requestIdSeed = 0;

            var s = LoadState();
            if (s.UnityPid == 0 || s.UnityPid != Process.GetCurrentProcess().Id) return false;
            if (s.AgentPid == 0 || s.AgentStdinHandle == 0 || s.AgentStdoutHandle == 0 || s.AgentStderrHandle == 0) return false;

            agentPid = s.AgentPid;
            stdinHandle = s.AgentStdinHandle;
            stdoutHandle = s.AgentStdoutHandle;
            stderrHandle = s.AgentStderrHandle;
            requestIdSeed = s.JsonRpcRequestIdSeed;
            return true;
        }

        public static void ClearLiveTransport()
        {
            var s = LoadState();
            s.UnityPid = 0;
            s.AgentPid = 0;
            s.AgentStdinHandle = 0;
            s.AgentStdoutHandle = 0;
            s.AgentStderrHandle = 0;
            s.JsonRpcRequestIdSeed = 0;
            s.LastUpdatedUtc = DateTime.UtcNow.ToString("o");
            SaveState(s);
        }

        public static void SaveSnapshot(string sessionId, IReadOnlyList<SessionUpdate> messages)
        {
            var s = LoadState();
            s.Version = CurrentVersion;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                s.SessionId = sessionId;
            }
            s.MessagesJson = SerializeMessages(messages);
            s.LastUpdatedUtc = DateTime.UtcNow.ToString("o");
            SaveState(s);
        }

        public static List<SessionUpdate> LoadMessages()
        {
            var json = LoadState().MessagesJson;
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<SessionUpdate>();
            }

            try
            {
                var token = JToken.Parse(json);
                var list = token.ToObject<List<SessionUpdate>>(AcpJson.Serializer);
                return list ?? new List<SessionUpdate>();
            }
            catch
            {
                // If parsing fails (schema change/corruption), fail soft by returning empty history.
                return new List<SessionUpdate>();
            }
        }

        public static void ClearSessionId()
        {
            var s = LoadState();
            s.SessionId = null;
            s.LastUpdatedUtc = DateTime.UtcNow.ToString("o");
            SaveState(s);
        }

        static string SerializeMessages(IReadOnlyList<SessionUpdate> messages)
        {
            try
            {
                return JToken.FromObject(messages ?? Array.Empty<SessionUpdate>(), AcpJson.Serializer)
                    .ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                return "[]";
            }
        }

        static string GetStatePath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
            return Path.Combine(projectRoot, "Library", "UnityAgentClient", "AgentSessionState.json");
        }

        static AgentSessionStateFile LoadState()
        {
            lock (fileLock)
            {
                if (cached != null) return cached;

                var path = GetStatePath();
                try
                {
                    if (!File.Exists(path))
                    {
                        cached = new AgentSessionStateFile { Version = CurrentVersion };
                        return cached;
                    }

                    var json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        cached = new AgentSessionStateFile { Version = CurrentVersion };
                        return cached;
                    }

                    var token = JToken.Parse(json);
                    cached = token.ToObject<AgentSessionStateFile>() ?? new AgentSessionStateFile { Version = CurrentVersion };
                    if (cached.Version <= 0) cached.Version = CurrentVersion;
                    return cached;
                }
                catch
                {
                    cached = new AgentSessionStateFile { Version = CurrentVersion };
                    return cached;
                }
            }
        }

        static void SaveState(AgentSessionStateFile state)
        {
            lock (fileLock)
            {
                cached = state ?? new AgentSessionStateFile { Version = CurrentVersion };

                try
                {
                    var path = GetStatePath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var json = JToken.FromObject(cached).ToString(Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText(path, json);
                }
                catch
                {
                    // ignore - persistence should never block editor actions or domain reload
                }
            }
        }
    }
}


