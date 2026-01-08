namespace AgentClientProtocol
{
public record InitializeRequest
{
    public ushort ProtocolVersion { get; init; }
    public ClientCapabilities ClientCapabilities { get; init; } = new();
    public Implementation? ClientInfo { get; init; }
}

}
