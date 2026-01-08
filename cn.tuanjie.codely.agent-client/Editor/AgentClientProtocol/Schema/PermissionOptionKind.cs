using System.Runtime.Serialization;

namespace AgentClientProtocol
{
public enum PermissionOptionKind
{
    [EnumMember(Value = "allow_once")]
    AllowOnce,

    [EnumMember(Value = "allow_always")]
    AllowAlways,

    [EnumMember(Value = "reject_once")]
    RejectOnce,

    [EnumMember(Value = "reject_always")]
    RejectAlways
}

}
