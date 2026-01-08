namespace AgentClientProtocol
{
public record PermissionOption
{
    public string OptionId { get; init; }
    public string Name { get; init; }
    public PermissionOptionKind Kind { get; init; }
}

}
