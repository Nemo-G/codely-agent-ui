namespace AgentClientProtocol
{
public record ClientCapabilities
{
    public FileSystemCapability Fs { get; init; } = new();
    public bool Terminal { get; init; } = false;
}

public record FileSystemCapability
{
    public bool ReadTextFile { get; init; } = false;
    public bool WriteTextFile { get; init; } = false;
}

}
