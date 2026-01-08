using System.Runtime.Serialization;

namespace AgentClientProtocol
{
public record PlanEntry
{
    public string Content { get; init; }
    public PlanEntryPriority Priority { get; init; }
    public PlanEntryStatus Status { get; init; }
}


public enum PlanEntryPriority
{
    [EnumMember(Value = "high")]
    High,

    [EnumMember(Value = "medium")]
    Medium,

    [EnumMember(Value = "low")]
    Low
}


public enum PlanEntryStatus
{
    [EnumMember(Value = "pending")]
    Pending,

    [EnumMember(Value = "in_progress")]
    InProgress,

    [EnumMember(Value = "completed")]
    Completed
}

}
