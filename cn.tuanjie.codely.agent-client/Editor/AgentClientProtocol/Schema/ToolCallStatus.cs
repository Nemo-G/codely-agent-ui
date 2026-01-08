using System.Runtime.Serialization;

namespace AgentClientProtocol
{
public enum ToolCallStatus
{
    [EnumMember(Value = "pending")]
    Pending,

    [EnumMember(Value = "in_progress")]
    InProgress,

    [EnumMember(Value = "completed")]
    Completed,

    [EnumMember(Value = "failed")]
    Failed
}

}
