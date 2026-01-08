namespace AgentClientProtocol
{
public record AgentCapabilities
{
    public bool LoadSession { get; init; } = false;
    public McpCapabilities McpCapabilities { get; init; } = new();
    public PromptCapabilities PromptCapabilities { get; init; } = new();
}

public record McpCapabilities
{
    public bool Http { get; init; } = false;
    public bool Sse { get; init; } = false;
}

public record PromptCapabilities
{
    public bool Audio { get; init; } = false;
    public bool EmbeddedContext { get; init; } = false;
    public bool Image { get; init; } = false;
}

}
