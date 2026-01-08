namespace AgentClientProtocol
{
public record LoadSessionResponse
{
    public SessionModelState? Models { get; init; }
    public SessionModeState? Modes { get; init; }
}

}
