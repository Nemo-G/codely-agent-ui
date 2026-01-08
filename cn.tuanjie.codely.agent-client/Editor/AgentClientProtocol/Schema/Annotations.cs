namespace AgentClientProtocol
{
public record Annotations
{
    public Role[]? Audience { get; init; }
    public string? LastModified { get; init; }
    public double? Priority { get; init; }
}

}
