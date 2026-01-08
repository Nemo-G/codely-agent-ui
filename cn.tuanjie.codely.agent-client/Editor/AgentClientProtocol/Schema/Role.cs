using System.Runtime.Serialization;

namespace AgentClientProtocol
{
public enum Role
{
    [EnumMember(Value = "assistant")]
    Assistant,

    [EnumMember(Value = "user")]
    User
}

}
