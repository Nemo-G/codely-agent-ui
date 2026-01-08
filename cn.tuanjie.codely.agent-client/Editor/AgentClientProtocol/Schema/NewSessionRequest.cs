using System;

namespace AgentClientProtocol
{
public record NewSessionRequest
{
    public string Cwd { get; init; }
    public McpServer[] McpServers { get; init; } = Array.Empty<McpServer>();
}

}
