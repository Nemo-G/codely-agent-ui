namespace AgentClientProtocol
{
public record TerminalOutputRequest
{
    public string SessionId { get; init; }
    public string TerminalId { get; init; }
}

}
