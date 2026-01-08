namespace AgentClientProtocol
{
public record WaitForTerminalExitRequest
{
    public string SessionId { get; init; }
    public string TerminalId { get; init; }
}

}
