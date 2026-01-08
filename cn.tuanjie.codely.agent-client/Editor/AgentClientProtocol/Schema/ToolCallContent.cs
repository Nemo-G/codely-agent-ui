using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public abstract record ToolCallContent;

public record ContentToolCallContent : ToolCallContent
{
    public ContentBlock Content { get; init; }
}

public record DiffToolCallContent : ToolCallContent
{
    public string Path { get; init; }
    public string NewText { get; init; }
    public string? OldText { get; init; }
}

public record TerminalToolCallContent : ToolCallContent
{
    public string TerminalId { get; init; }
}

public sealed class ToolCallContentJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(ToolCallContent);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        var obj = JObject.Load(reader);

        if (obj.TryGetValue("content", out _))
        {
            return obj.ToObject<ContentToolCallContent>(serializer);
        }

        if (obj.TryGetValue("terminalId", out _))
        {
            return obj.ToObject<TerminalToolCallContent>(serializer);
        }

        if (obj.TryGetValue("path", out _) || obj.TryGetValue("newText", out _))
        {
            return obj.ToObject<DiffToolCallContent>(serializer);
        }

        throw new JsonSerializationException("Unknown ToolCallContent type - missing discriminator properties");
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value, value.GetType());
    }
}

}
