namespace AgentClientProtocol
{
public record CreateTerminalRequest
{
    public string SessionId { get; init; }
    public string Command { get; init; }
    public string[] Args { get; init; } = new string[0];
    public string? Cwd { get; init; }
    public EnvVariable[] Env { get; init; } = new EnvVariable[0];
    public ulong? OutputByteLimit { get; init; }
}

}
