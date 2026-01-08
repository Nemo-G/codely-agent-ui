namespace AgentClientProtocol
{
public record TerminalExitStatus
{
    public uint? ExitCode { get; init; }
    public string? Signal { get; init; }
}

}
