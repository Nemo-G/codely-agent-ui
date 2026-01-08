using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public interface IAcpAgent
{
    ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken = default);
    ValueTask<AuthenticateResponse> AuthenticateAsync(AuthenticateRequest request, CancellationToken cancellationToken = default);
    ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken = default);
    ValueTask<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken = default);
    ValueTask CancelAsync(CancelNotification notification, CancellationToken cancellationToken = default);
    ValueTask<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken = default);
    ValueTask<SetSessionModeResponse> SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken = default);
    ValueTask<SetSessionModelResponse> SetSessionModelAsync(SetSessionModelRequest request, CancellationToken cancellationToken = default);
    ValueTask<JToken> ExtMethodAsync(string method, JToken request, CancellationToken cancellationToken = default);
    ValueTask ExtNotificationAsync(string method, JToken notification, CancellationToken cancellationToken = default);
}
}
