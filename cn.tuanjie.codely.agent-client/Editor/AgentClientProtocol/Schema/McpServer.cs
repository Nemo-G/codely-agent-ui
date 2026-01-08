using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public abstract record McpServer
{
    public string Name { get; init; }
    public abstract string Type { get; }
}

public record HttpMcpServer : McpServer
{
    public override string Type => "http";
    public string Url { get; init; }
    public HttpHeader[] Headers { get; init; }
}

public record SseMcpServer : McpServer
{
    public override string Type => "sse";
    public string Url { get; init; }
    public HttpHeader[] Headers { get; init; }
}

public record StdioMcpServer : McpServer
{
    public override string Type => "stdio";
    public string Command { get; init; }
    public string[] Args { get; init; }
    public EnvVariable[] Env { get; init; }
}

public sealed class McpServerJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(McpServer);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        var obj = JObject.Load(reader);

        var type = obj["type"]?.Value<string>();
        switch (type)
        {
            case "http":
                return obj.ToObject<HttpMcpServer>(serializer);
            case "sse":
                return obj.ToObject<SseMcpServer>(serializer);
            case "stdio":
                return obj.ToObject<StdioMcpServer>(serializer);
            default:
                throw new JsonSerializationException($"Unknown McpServer type: {type}");
        }
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value, value.GetType());
    }
}
}
