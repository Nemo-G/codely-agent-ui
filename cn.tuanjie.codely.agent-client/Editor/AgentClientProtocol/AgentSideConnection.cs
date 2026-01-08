using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public sealed class AgentSideConnection : IDisposable
{
    readonly CancellationTokenSource cts = new();
    readonly JsonRpcEndpoint endpoint;

    public AgentSideConnection(IAcpAgent agent, TextReader reader, TextWriter writer)
    {
        endpoint = new(
            _ => new(reader.ReadLine()),
            (s, _) =>
            {
                writer.WriteLine(s);
                return default;
            },
            (s, _) => default
        );

        endpoint.SetRequestHandler(AgentMethods.Initialize, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<InitializeRequest>(AcpJson.Serializer);
            var response = await agent.InitializeAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(AgentMethods.Authenticate, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<AuthenticateRequest>(AcpJson.Serializer);
            var response = await agent.AuthenticateAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(AgentMethods.SessionNew, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<NewSessionRequest>(AcpJson.Serializer);
            var response = await agent.NewSessionAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(AgentMethods.SessionPrompt, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<PromptRequest>(AcpJson.Serializer);
            var response = await agent.PromptAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(AgentMethods.SessionLoad, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<LoadSessionRequest>(AcpJson.Serializer);
            var response = await agent.LoadSessionAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(AgentMethods.SessionSetMode, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<SetSessionModeRequest>(AcpJson.Serializer);
            var response = await agent.SetSessionModeAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetRequestHandler(AgentMethods.SessionSetModel, async (request, ct) =>
        {
            AcpException.ThrowIfParamIsNull(request.Params);

            var args = request.Params.ToObject<SetSessionModelRequest>(AcpJson.Serializer);
            var response = await agent.SetSessionModelAsync(args, ct);

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JToken.FromObject(response, AcpJson.Serializer)
            };
        });

        endpoint.SetNotificationHandler(AgentMethods.SessionCancel, async (notification, ct) =>
        {
            AcpException.ThrowIfParamIsNull(notification.Params);

            var cancelNotification = notification.Params.ToObject<CancelNotification>(AcpJson.Serializer);

            await agent.CancelAsync(cancelNotification, ct);
        });

        endpoint.SetDefaultRequestHandler(async (request, ct) =>
        {
            var response = await agent.ExtMethodAsync(request.Method, request.Params ?? JValue.CreateNull(), ct);
            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = response
            };
        });

        endpoint.SetDefaultNotificationHandler(async (notification, ct) =>
        {
            await agent.ExtNotificationAsync(notification.Method, notification.Params ?? JValue.CreateNull(), ct);
        });
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
