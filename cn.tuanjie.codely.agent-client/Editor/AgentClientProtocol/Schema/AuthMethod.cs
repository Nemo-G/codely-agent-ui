namespace AgentClientProtocol
{
public record AuthMethod
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string? Description { get; init; }
}

}
