using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public abstract record ContentBlock
{
    public abstract string Type { get; }
    public Annotations? Annotations { get; init; }
}

public record TextContentBlock : ContentBlock
{
    public override string Type => "text";
    public string Text { get; init; }
}

public record ImageContentBlock : ContentBlock
{
    public override string Type => "image";
    public string Data { get; init; }
    public string MimeType { get; init; }
    public string? Uri { get; init; }
}

public record AudioContentBlock : ContentBlock
{
    public override string Type => "audio";
    public string Data { get; init; }
    public string MimeType { get; init; }
}

public record ResourceLinkContentBlock : ContentBlock
{
    public override string Type => "resource_link";
    public string Name { get; init; }
    public string Uri { get; init; }
    public string? Description { get; init; }
    public string? MimeType { get; init; }
    public long? Size { get; init; }
    public string? Title { get; init; }
}

public record ResourceContentBlock : ContentBlock
{
    public override string Type => "resource";
    public EmbeddedResourceResource Resource { get; init; }
}

public sealed class ContentBlockJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(ContentBlock);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        var obj = JObject.Load(reader);

        var type = obj["type"]?.Value<string>();
        switch (type)
        {
            case "text":
                return obj.ToObject<TextContentBlock>(serializer);
            case "image":
                return obj.ToObject<ImageContentBlock>(serializer);
            case "audio":
                return obj.ToObject<AudioContentBlock>(serializer);
            case "resource_link":
                return obj.ToObject<ResourceLinkContentBlock>(serializer);
            case "resource":
                return obj.ToObject<ResourceContentBlock>(serializer);
            default:
                throw new JsonSerializationException($"Unknown ContentBlock type: {type}");
        }
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value, value.GetType());
    }
}
}
