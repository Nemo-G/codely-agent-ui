namespace AgentClientProtocol
{
public record LoadSessionRequest
{
    public McpServer[] McpServers { get; init; }
    public string Cwd { get; init; }
    public string SessionId { get; init; }
}

}
