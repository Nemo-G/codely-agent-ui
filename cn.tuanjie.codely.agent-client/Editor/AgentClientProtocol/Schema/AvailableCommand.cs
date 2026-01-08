namespace AgentClientProtocol
{
public record AvailableCommand
{
    public string Name { get; init; }
    public string Description { get; init; }
    public AvailableCommandInput? Input { get; init; }
}

}
