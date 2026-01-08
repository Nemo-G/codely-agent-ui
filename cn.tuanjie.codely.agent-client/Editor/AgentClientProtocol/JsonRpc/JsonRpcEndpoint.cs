using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AgentClientProtocol
{
internal enum JsonRpcErrorCode
{
    ParseError = -32700,
    InvalidRequest = -32600,
    MethodNotFound = -32601,
    InvalidParams = -32602,
    InternalError = -32603
}

internal sealed class JsonRpcEndpoint
{
    readonly Func<CancellationToken, ValueTask<string?>> readFunc;
    readonly Func<string, CancellationToken, ValueTask> writeFunc;
    readonly Func<string, CancellationToken, ValueTask> errorWriteFunc;

    readonly ConcurrentDictionary<RequestId, TaskCompletionSource<JsonRpcResponse>> pendingRequests = new();
    readonly ConcurrentDictionary<string, Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>>> requestHandlers = new();
    readonly ConcurrentDictionary<string, Func<JsonRpcNotification, CancellationToken, ValueTask>> notificationHandlers = new();
    Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>>? defaultRequestHandler;
    Func<JsonRpcNotification, CancellationToken, ValueTask>? defaultNotificationHandler;
    int nextRequestId;

    public JsonRpcEndpoint(
        Func<CancellationToken, ValueTask<string?>> readFunc,
        Func<string, CancellationToken, ValueTask> writeFunc,
        Func<string, CancellationToken, ValueTask> errorWriteFunc,
        int initialRequestId = 0)
    {
        this.readFunc = readFunc;
        this.writeFunc = writeFunc;
        this.errorWriteFunc = errorWriteFunc;
        nextRequestId = initialRequestId;
    }

    public void SetRequestHandler(string method, Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>> handler)
    {
        requestHandlers.TryAdd(method, handler);
    }

    public void SetNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler)
    {
        notificationHandlers.TryAdd(method, handler);
    }

    public void SetDefaultRequestHandler(Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>> handler)
    {
        defaultRequestHandler = handler;
    }

    public void SetDefaultNotificationHandler(Func<JsonRpcNotification, CancellationToken, ValueTask> handler)
    {
        defaultNotificationHandler = handler;
    }

    public async Task ReadMessagesAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var line = await readFunc(cancellationToken).ConfigureAwait(false);
                if (line == null) return; // EOF (e.g., agent process exited / pipe closed)
                if (string.IsNullOrWhiteSpace(line)) continue;

                var trimmed = line.Trim();
                if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}') continue; // skip non-json input

                var message = JsonConvert.DeserializeObject<JsonRpcMessage>(line, AcpJson.Settings);
                if (message == null) continue;

                switch (message)
                {
                    case JsonRpcRequest request:
                        try
                        {
                            if (requestHandlers.TryGetValue(request.Method, out var requestHandler))
                            {
                                var response = await requestHandler(request, cancellationToken);
                                await writeFunc(JsonConvert.SerializeObject(response, AcpJson.Settings), cancellationToken);
                            }
                            else if (defaultRequestHandler != null)
                            {
                                var response = await defaultRequestHandler(request, cancellationToken);
                                await writeFunc(JsonConvert.SerializeObject(response, AcpJson.Settings), cancellationToken);
                            }
                            else
                            {
                                await writeFunc(JsonConvert.SerializeObject(new JsonRpcResponse
                                {
                                    Id = request.Id,
                                    Error = new()
                                    {
                                        Code = (int)JsonRpcErrorCode.MethodNotFound,
                                        Message = $"Method '{request.Method}' is not available",
                                    }
                                }, AcpJson.Settings), cancellationToken);
                            }
                        }
                        catch (NotImplementedException)
                        {
                            await writeFunc(JsonConvert.SerializeObject(new JsonRpcResponse
                            {
                                Id = request.Id,
                                Error = new()
                                {
                                    Code = (int)JsonRpcErrorCode.MethodNotFound,
                                    Message = $"Method '{request.Method}' is not available",
                                }
                            }, AcpJson.Settings), cancellationToken);
                        }
                        catch (AcpException acpException)
                        {
                            await writeFunc(JsonConvert.SerializeObject(new JsonRpcResponse
                            {
                                Id = request.Id,
                                Error = new()
                                {
                                    Code = acpException.Code,
                                    Data = acpException.ErrorData,
                                    Message = acpException.Message,
                                }
                            }, AcpJson.Settings), cancellationToken);
                        }
                        break;
                    case JsonRpcResponse response:
                        {
                            if (pendingRequests.TryRemove(response.Id, out var tcs))
                            {
                                tcs.TrySetResult(response);
                            }
                        }
                        break;
                    case JsonRpcNotification notification:
                        if (notificationHandlers.TryGetValue(notification.Method, out var notificationHandler))
                        {
                            await notificationHandler(notification, cancellationToken);
                        }
                        else if (defaultNotificationHandler != null)
                        {
                            await defaultNotificationHandler(notification, cancellationToken);
                        }
                        break;
                    default:
                        throw new AcpException($"Invalid response type: {message?.GetType().Name}", null, (int)JsonRpcErrorCode.InternalError);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await errorWriteFunc(ex.ToString(), cancellationToken);
            }
        }
    }

    public async ValueTask SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (message is JsonRpcRequest request && !request.Id.IsValid)
        {
            request.Id = Interlocked.Increment(ref nextRequestId);
        }

        var json = JsonConvert.SerializeObject(message, AcpJson.Settings);
        await writeFunc(json, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Id.IsValid) request.Id = Interlocked.Increment(ref nextRequestId);

        var json = JsonConvert.SerializeObject(request, AcpJson.Settings);

        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingRequests.TryAdd(request.Id, tcs);

        CancellationTokenRegistration ctr = default;
        if (cancellationToken.CanBeCanceled)
        {
            ctr = cancellationToken.Register(() =>
            {
                if (pendingRequests.TryRemove(request.Id, out var removed))
                {
                    removed.TrySetCanceled(cancellationToken);
                }
            });
        }

        try
        {
            await writeFunc(json, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (pendingRequests.TryRemove(request.Id, out var removed))
            {
                removed.TrySetException(ex);
            }
            throw;
        }
        finally
        {
            ctr.Dispose();
        }
    }
}
}
