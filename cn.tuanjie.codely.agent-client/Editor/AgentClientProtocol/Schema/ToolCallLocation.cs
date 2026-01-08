namespace AgentClientProtocol
{
public record ToolCallLocation
{
    public string Path { get; init; }
    public uint? Line { get; init; }
}

}
