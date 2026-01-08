using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public abstract record EmbeddedResourceResource;

public record TextResourceContents : EmbeddedResourceResource
{
    public string Uri { get; init; }
    public string Text { get; init; }
    public string? MimeType { get; init; }
}

public record BlobResourceContents : EmbeddedResourceResource
{
    public string Blob { get; init; }
    public string Uri { get; init; }
    public string? MimeType { get; init; }
}

public sealed class EmbeddedResourceResourceJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(EmbeddedResourceResource);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        var obj = JObject.Load(reader);

        if (obj.TryGetValue("text", out _))
        {
            return obj.ToObject<TextResourceContents>(serializer);
        }

        if (obj.TryGetValue("blob", out _))
        {
            return obj.ToObject<BlobResourceContents>(serializer);
        }

        throw new JsonSerializationException("Unknown EmbeddedResourceResource type - missing 'text' or 'blob' property");
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value, value.GetType());
    }
}
}
