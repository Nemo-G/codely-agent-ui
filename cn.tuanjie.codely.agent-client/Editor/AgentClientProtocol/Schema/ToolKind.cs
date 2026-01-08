using System.Runtime.Serialization;

namespace AgentClientProtocol
{
public enum ToolKind
{
    [EnumMember(Value = "read")]
    Read,

    [EnumMember(Value = "edit")]
    Edit,

    [EnumMember(Value = "delete")]
    Delete,

    [EnumMember(Value = "move")]
    Move,

    [EnumMember(Value = "search")]
    Search,

    [EnumMember(Value = "execute")]
    Execute,

    [EnumMember(Value = "think")]
    Think,

    [EnumMember(Value = "fetch")]
    Fetch,

    [EnumMember(Value = "switch_mode")]
    SwitchMode,

    [EnumMember(Value = "other")]
    Other
}

}
