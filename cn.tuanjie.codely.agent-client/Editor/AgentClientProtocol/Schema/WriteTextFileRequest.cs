namespace AgentClientProtocol
{
public record WriteTextFileRequest
{
    public string SessionId { get; init; }
    public string Path { get; init; }
    public string Content { get; init; }
}

}
