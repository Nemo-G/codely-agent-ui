namespace AgentClientProtocol
{
public record SetSessionModeRequest
{
    public string SessionId { get; init; }
    public string ModeId { get; init; }
}

}
