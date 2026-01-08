using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public abstract record SessionUpdate
{
    public abstract string Update { get; }
}

public sealed class SessionUpdateJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(SessionUpdate);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        var obj = JObject.Load(reader);

        var type = obj["sessionUpdate"]?.Value<string>();
        switch (type)
        {
            case "user_message_chunk":
                return obj.ToObject<UserMessageChunkSessionUpdate>(serializer);
            case "agent_message_chunk":
                return obj.ToObject<AgentMessageChunkSessionUpdate>(serializer);
            case "agent_thought_chunk":
                return obj.ToObject<AgentThoughtChunkSessionUpdate>(serializer);
            case "tool_call":
                return obj.ToObject<ToolCallSessionUpdate>(serializer);
            case "tool_call_update":
                return obj.ToObject<ToolCallUpdateSessionUpdate>(serializer);
            case "plan":
                return obj.ToObject<PlanSessionUpdate>(serializer);
            case "available_commands_update":
                return obj.ToObject<AvailableCommandsUpdateSessionUpdate>(serializer);
            case "current_mode_update":
                return obj.ToObject<CurrentModeUpdateSessionUpdate>(serializer);
            default:
                throw new JsonSerializationException($"Unknown SessionUpdate type: {type}");
        }
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value, value.GetType());
    }
}

public record UserMessageChunkSessionUpdate : SessionUpdate
{
    public override string Update => "user_message_chunk";
    public ContentBlock Content { get; init; }
}

public record AgentMessageChunkSessionUpdate : SessionUpdate
{
    public override string Update => "agent_message_chunk";
    public ContentBlock Content { get; init; }
}

public record AgentThoughtChunkSessionUpdate : SessionUpdate
{
    public override string Update => "agent_thought_chunk";
    public ContentBlock Content { get; init; }
}

public record ToolCallSessionUpdate : SessionUpdate
{
    public override string Update => "tool_call";
    public string ToolCallId { get; init; }
    public string Title { get; init; }
    public ToolCallContent[] Content { get; init; } = new ToolCallContent[0];
    public ToolKind Kind { get; init; }
    public ToolCallLocation[] Locations { get; init; } = new ToolCallLocation[0];
    public JToken RawInput { get; init; }
    public JToken RawOutput { get; init; }
    public ToolCallStatus Status { get; init; }
}

public record ToolCallUpdateSessionUpdate : SessionUpdate
{
    public override string Update => "tool_call_update";
    public string ToolCallId { get; init; }
    public ToolCallContent[]? Content { get; init; }
    public ToolKind? Kind { get; init; }
    public ToolCallLocation[]? Locations { get; init; }
    public JToken RawInput { get; init; }
    public JToken RawOutput { get; init; }
    public ToolCallStatus? Status { get; init; }
    public string? Title { get; init; }
}

public record PlanSessionUpdate : SessionUpdate
{
    public override string Update => "plan";
    public PlanEntry[] Entries { get; init; }
}

public record AvailableCommandsUpdateSessionUpdate : SessionUpdate
{
    public override string Update => "available_commands_update";
    public AvailableCommand[] AvailableCommands { get; init; }
}

public record CurrentModeUpdateSessionUpdate : SessionUpdate
{
    public override string Update => "current_mode_update";
    public string CurrentModeId { get; init; }
}

}
