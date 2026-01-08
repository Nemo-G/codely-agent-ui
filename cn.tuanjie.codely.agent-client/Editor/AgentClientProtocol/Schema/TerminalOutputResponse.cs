namespace AgentClientProtocol
{
public record TerminalOutputResponse
{
    public string Output { get; init; }
    public bool Truncated { get; init; }
    public TerminalExitStatus? ExitStatus { get; init; }
}

}
