namespace AgentClientProtocol
{
public record PromptRequest
{
    public string SessionId { get; init; }
    public ContentBlock[] Prompt { get; init; }
}

}
