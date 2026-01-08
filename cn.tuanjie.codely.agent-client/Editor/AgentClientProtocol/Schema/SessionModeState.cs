namespace AgentClientProtocol
{
public record SessionModeState
{
    public string CurrentModeId { get; init; }
    public SessionMode[] AvailableModes { get; init; }
}

}
