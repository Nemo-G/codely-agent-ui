using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public sealed class ClientSideConnection : IDisposable, IAcpAgent
{
    readonly IAcpClient client;

    readonly CancellationTokenSource cts = new();
    readonly JsonRpcEndpoint endpoint;

    public ClientSideConnection(Func<IAcpAgent, IAcpClient> toClient, TextReader reader, TextWriter writer, int initialRequestId = 0)
    {
        client = toClient(this);

        endpoint = new(
            _ => new(reader.ReadLine()),
            (s, _) =>
            {
                writer.WriteLine(s);
                return default;
            },
            (s, _) => default,
            initialRequestId
        );

        endpoint.SetRequestHandler(ClientMethods.FsReadTextFile, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<ReadTextFileRequest>(AcpJson.Serializer);
            var response = await client.ReadTextFileAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(ClientMethods.FsWriteTextFile, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<WriteTextFileRequest>(AcpJson.Serializer);
            var response = await client.WriteTextFileAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(ClientMethods.SessionRequestPermission, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<RequestPermissionRequest>(AcpJson.Serializer);
            var response = await client.RequestPermissionAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(ClientMethods.TerminalCreate, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<CreateTerminalRequest>(AcpJson.Serializer);
            var response = await client.CreateTerminalAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(ClientMethods.TerminalKill, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<KillTerminalCommandRequest>(AcpJson.Serializer);
            var response = await client.KillTerminalCommandAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(ClientMethods.TerminalOutput, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<TerminalOutputRequest>(AcpJson.Serializer);
            var response = await client.TerminalOutputAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(ClientMethods.TerminalRelease, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<ReleaseTerminalRequest>(AcpJson.Serializer);
            var response = await client.ReleaseTerminalAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(ClientMethods.TerminalWaitForExit, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<WaitForTerminalExitRequest>(AcpJson.Serializer);
            var response = await client.WaitForTerminalExitAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetNotificationHandler(ClientMethods.SessionUpdate, async (notification, ct) =>
        {
            AcpException.ThrowIfParamIsNull(notification.Params);

            var sessionNotification = notification.Params.ToObject<SessionNotification>(AcpJson.Serializer);

            await client.SessionNotificationAsync(sessionNotification, ct);
        });

        endpoint.SetDefaultRequestHandler(async (request, ct) =>
        {
            var response = await client.ExtMethodAsync(request.Method, request.Params ?? JValue.CreateNull(), ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = response
            };
        });

        endpoint.SetDefaultNotificationHandler(async (notification, ct) =>
        {
            await client.ExtNotificationAsync(notification.Method, notification.Params ?? JValue.CreateNull(), ct);
        });
    }

    async ValueTask<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken)
    {
        var response = await endpoint.SendRequestAsync(new JsonRpcRequest
        {
            Method = method,
            Id = default,
            Params = JToken.FromObject(request, AcpJson.Serializer)
        }, cancellationToken);

        if (response.Error != null)
        {
            throw new AcpException($"{response.Error!.Message}", response.Error.Data, response.Error.Code);
        }

        // HACK: 
        // In a specific version of Gemini-CLI, the `authenticate` method returns a response (`result: null`) 
        // that differs from the expected schema. To accommodate this, we are ignoring the null check. 
        // Since `result` should not be null in any other case, this should generally not be a problem.
        if (response.Result == null || response.Result.Type == JTokenType.Null)
        {
            return default!;
        }

        return response.Result.ToObject<TResponse>(AcpJson.Serializer);
    }


    async ValueTask NotificationAsync<TNotification>(string method, TNotification notification, CancellationToken cancellationToken)
    {
        await endpoint.SendMessageAsync(new JsonRpcNotification
        {
            Method = method,
            Params = JToken.FromObject(notification, AcpJson.Serializer)
        }, cancellationToken);
    }

    public ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken = default)
    {
        return RequestAsync<InitializeRequest, InitializeResponse>(AgentMethods.Initialize, request, cancellationToken);
    }

    public ValueTask<AuthenticateResponse> AuthenticateAsync(AuthenticateRequest request, CancellationToken cancellationToken = default)
    {
        return RequestAsync<AuthenticateRequest, AuthenticateResponse>(AgentMethods.Authenticate, request, cancellationToken);
    }

    public ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken = default)
    {
        return RequestAsync<NewSessionRequest, NewSessionResponse>(AgentMethods.SessionNew, request, cancellationToken);
    }

    public ValueTask<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken = default)
    {
        return RequestAsync<PromptRequest, PromptResponse>(AgentMethods.SessionPrompt, request, cancellationToken);
    }

    public ValueTask CancelAsync(CancelNotification notification, CancellationToken cancellationToken = default)
    {
        return NotificationAsync(AgentMethods.SessionCancel, notification, cancellationToken);
    }

    public ValueTask<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken = default)
    {
        return RequestAsync<LoadSessionRequest, LoadSessionResponse>(AgentMethods.SessionLoad, request, cancellationToken);
    }

    public ValueTask<SetSessionModeResponse> SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken = default)
    {
        return RequestAsync<SetSessionModeRequest, SetSessionModeResponse>(AgentMethods.SessionSetMode, request, cancellationToken);
    }

    public ValueTask<SetSessionModelResponse> SetSessionModelAsync(SetSessionModelRequest request, CancellationToken cancellationToken = default)
    {
        return RequestAsync<SetSessionModelRequest, SetSessionModelResponse>(AgentMethods.SessionSetModel, request, cancellationToken);
    }

    public async ValueTask<JToken> ExtMethodAsync(string method, JToken request, CancellationToken cancellationToken = default)
    {
        var response = await endpoint.SendRequestAsync(new JsonRpcRequest
        {
            Method = method,
            Id = default,
            Params = request,
        }, cancellationToken);

        if (response.Result == null || response.Result.Type == JTokenType.Null)
        {
            throw new AcpException($"{response.Error!.Message}", response.Error.Data, response.Error.Code);
        }

        return response.Result;
    }

    public ValueTask ExtNotificationAsync(string method, JToken notification, CancellationToken cancellationToken = default)
    {
        // writer.WriteLineAsync(notification.ToString());
        return default;
    }

    public void Dispose()
    {
        cts.Cancel();
        cts.Dispose();
    }

    public void Open()
    {
        Task.Run(async () => await endpoint.ReadMessagesAsync(cts.Token));
    }
}
}
