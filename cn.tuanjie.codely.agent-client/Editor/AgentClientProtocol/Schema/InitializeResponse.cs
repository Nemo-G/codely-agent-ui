namespace AgentClientProtocol
{
public record InitializeResponse
{
    public ushort ProtocolVersion { get; init; }
    public AgentCapabilities AgentCapabilities { get; init; } = new();
    public Implementation? AgentInfo { get; init; }
    public AuthMethod[] AuthMethods { get; init; } = new AuthMethod[0];
}

}
