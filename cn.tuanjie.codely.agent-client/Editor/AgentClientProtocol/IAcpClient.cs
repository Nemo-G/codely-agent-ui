using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public interface IAcpClient
{
    ValueTask<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken = default);
    ValueTask SessionNotificationAsync(SessionNotification notification, CancellationToken cancellationToken = default);
    ValueTask<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken = default);
    ValueTask<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken = default);
    ValueTask<CreateTerminalResponse> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default);
    ValueTask<TerminalOutputRequest> TerminalOutputAsync(TerminalOutputRequest request, CancellationToken cancellationToken = default);
    ValueTask<ReleaseTerminalResponse> ReleaseTerminalAsync(ReleaseTerminalRequest request, CancellationToken cancellationToken = default);
    ValueTask<WaitForTerminalExitResponse> WaitForTerminalExitAsync(WaitForTerminalExitRequest request, CancellationToken cancellationToken = default);
    ValueTask<KillTerminalCommandResponse> KillTerminalCommandAsync(KillTerminalCommandRequest request, CancellationToken cancellationToken = default);
    ValueTask<JToken> ExtMethodAsync(string method, JToken request, CancellationToken cancellationToken = default);
    ValueTask ExtNotificationAsync(string method, JToken notification, CancellationToken cancellationToken = default);
}
}
