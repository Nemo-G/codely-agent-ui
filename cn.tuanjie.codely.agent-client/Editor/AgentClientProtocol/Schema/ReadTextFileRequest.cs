namespace AgentClientProtocol
{
public record ReadTextFileRequest
{
    public string SessionId { get; init; }
    public string Path { get; init; }
    public uint? Limit { get; init; }
    public uint? Line { get; init; }
}

}
