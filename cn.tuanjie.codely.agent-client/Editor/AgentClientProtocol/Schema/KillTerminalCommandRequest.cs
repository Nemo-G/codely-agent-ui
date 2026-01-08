namespace AgentClientProtocol
{
public record KillTerminalCommandRequest
{
    public string SessionId { get; init; }
    public string TerminalId { get; init; }
}

}
