namespace AgentClientProtocol
{
public record SessionNotification
{
    public string SessionId { get; init; }
    public SessionUpdate Update { get; init; }
}

}
