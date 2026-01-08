namespace AgentClientProtocol
{
public record WaitForTerminalExitResponse
{
    public uint? ExitCode { get; init; }
    public string? Signal { get; init; }
}

}
