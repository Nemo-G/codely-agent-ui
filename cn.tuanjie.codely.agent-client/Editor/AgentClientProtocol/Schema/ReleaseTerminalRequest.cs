namespace AgentClientProtocol
{
public record ReleaseTerminalRequest
{
    public string SessionId { get; init; }
    public string TerminalId { get; init; }
}

}
