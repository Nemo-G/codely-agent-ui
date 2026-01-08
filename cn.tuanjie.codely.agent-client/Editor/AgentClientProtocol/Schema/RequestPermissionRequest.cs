namespace AgentClientProtocol
{
using Newtonsoft.Json.Linq;

public record RequestPermissionRequest
{
    public string SessionId { get; init; }
    public JToken ToolCall { get; init; }
    public PermissionOption[] Options { get; init; }
}

}
