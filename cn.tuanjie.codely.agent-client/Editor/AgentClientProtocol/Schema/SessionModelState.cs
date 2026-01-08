namespace AgentClientProtocol
{
public record SessionModelState
{
    public string CurrentModelId { get; init; }
    public ModelInfo[] AvailableModels { get; init; }
}

}
