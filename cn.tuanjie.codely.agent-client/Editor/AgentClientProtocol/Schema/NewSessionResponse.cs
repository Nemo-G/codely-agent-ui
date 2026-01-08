namespace AgentClientProtocol
{
public record NewSessionResponse
{
    public string SessionId { get; init; }
    public SessionModelState? Models { get; init; }
    public SessionModeState? Modes { get; init; }
}

}
