namespace AgentClientProtocol
{
public record SetSessionModelRequest
{
    public string SessionId { get; init; }
    public string ModelId { get; init; }
}

}
