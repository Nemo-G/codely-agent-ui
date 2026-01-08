using System.Runtime.Serialization;

namespace AgentClientProtocol
{
public enum StopReason
{
    [EnumMember(Value = "end_turn")]
    EndTurn,

    [EnumMember(Value = "max_tokens")]
    MaxTokens,

    [EnumMember(Value = "max_turn_requests")]
    MaxTurnRequests,

    [EnumMember(Value = "refusal")]
    Refusal,

    [EnumMember(Value = "cancelled")]
    Cancelled
}

}
