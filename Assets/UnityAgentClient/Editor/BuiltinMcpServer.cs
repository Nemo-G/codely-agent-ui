using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityAgentClient
{
    [InitializeOnLoad]
    public static class BuiltinMcpServer
    {
        static HttpListener listener;
        static Thread listenerThread;
        const int Port = 57123;
        static readonly string Url = $"http://localhost:{Port}/";

        static readonly List<LogEntry> collectedLogs = new();
        static readonly object logLock = new();

        static BuiltinMcpServer()
        {
            Application.logMessageReceived += OnLogMessageReceived;
            EditorApplication.update += Initialize;
        }

        static void Initialize()
        {
            EditorApplication.update -= Initialize;
            StartServer();
            EditorApplication.quitting += StopServer;
        }

        static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            lock (logLock)
            {
                collectedLogs.Add(new LogEntry
                {
                    Condition = condition,
                    StackTrace = stackTrace,
                    Type = type.ToString()
                });
            }
        }

        static void StartServer()
        {
            if (listener != null) return;

            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add(Url);
                listener.Start();

                listenerThread = new Thread(HandleRequests)
                {
                    IsBackground = true
                };
                listenerThread.Start();

                Logger.LogVerbose($"Built-in MCP Server Started on port {Port}");
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to start MCP Server: {e.Message}");
            }
        }

        static void StopServer()
        {
            Application.logMessageReceived -= OnLogMessageReceived;
            if (listener != null)
            {
                listener.Stop();
                listener.Close();
                listener = null;
                Logger.LogVerbose("Built-in MCP server Stopped");
            }
        }

        static void HandleRequests()
        {
            while (listener != null && listener.IsListening)
            {
                try
                {
                    var context = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => ProcessRequest(context));
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Logger.LogError($"Request handling error: {e.Message}");
                }
            }
        }

        static void ProcessRequest(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                response.AddHeader("Access-Control-Allow-Origin", "*");
                response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                string responseString;


                if (request.Url.AbsolutePath == "/tools" && request.HttpMethod == "GET")
                {
                    responseString = GetToolsList();
                }
                else if (request.Url.AbsolutePath == "/read_unity_console" && request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                    var body = reader.ReadToEnd();
                    responseString = HandleGetLogs(body);
                }
                else
                {
                    response.StatusCode = 404;
                    responseString = "{\"error\":\"Not Found\"}";
                }

                var buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
            }
            catch (Exception e)
            {
                Logger.LogError($"Processing error: {e.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        static string GetToolsList()
        {
            var tools = new[]
            {
                new
                {
                    name = "read_unity_console",
                    description = "Retrieve Unity Editor console logs.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            maxCount = new
                            {
                                type = "number",
                                description = "Maximum number of logs to retrieve (default: 100).",
                                @default = 100
                            }
                        }
                    }
                }
            };

            return JsonSerializer.Serialize(new { tools });
        }

        static string HandleGetLogs(string requestBody)
        {
            try
            {
                var args = string.IsNullOrEmpty(requestBody)
                    ? new GetLogsArguments()
                    : JsonSerializer.Deserialize<GetLogsArguments>(requestBody);

                if (args.maxCount <= 0) args.maxCount = 100;

                List<LogEntry> logs;
                lock (logLock)
                {
                    var startIndex = Math.Max(0, collectedLogs.Count - args.maxCount);
                    var count = Math.Min(args.maxCount, collectedLogs.Count);
                    logs = collectedLogs.GetRange(startIndex, count);
                }

                var result = new StringBuilder();
                result.AppendLine($"number of logs: {logs.Count}");
                result.AppendLine();

                foreach (var log in logs)
                {
                    result.AppendLine($"[{log.Type.ToUpper()}]");
                    result.AppendLine(log.Condition);
                    result.AppendLine();
                }

                return $"{{\"result\":\"{EscapeJson(result.ToString())}\"}}";
            }
            catch (Exception e)
            {
                return $"{{\"error\":\"{EscapeJson(e.Message)}\"}}";
            }
        }

        static string EscapeJson(string text)
        {
            return text
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        class LogEntry
        {
            public string Condition { get; set; }
            public string StackTrace { get; set; }
            public string Type { get; set; }
        }

        [Serializable]
        class GetLogsArguments
        {
            public int maxCount = 100;
        }
    }
}
