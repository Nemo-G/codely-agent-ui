using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public abstract record JsonRpcMessage
{
    [JsonProperty("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";
}

public record JsonRpcRequest : JsonRpcMessage
{
    public RequestId Id { get; set; }
    public string Method { get; set; }
    public JToken Params { get; set; }
}

public record JsonRpcResponse : JsonRpcMessage
{
    public RequestId Id { get; set; }
    public JToken Result { get; set; }
    public JsonRpcError Error { get; set; }
}

public record JsonRpcNotification : JsonRpcMessage
{
    public string Method { get; set; }
    public JToken Params { get; set; }
}

public record JsonRpcError
{
    public int Code { get; set; }
    public string Message { get; set; }
    public JToken Data { get; set; }
}

public sealed class JsonRpcMessageJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(JsonRpcMessage);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        var root = JObject.Load(reader);

        var version = root["jsonrpc"]?.Value<string>();
        if (version != "2.0")
        {
            throw new JsonSerializationException("Invalid jsonrpc version");
        }

        var hasId = root.TryGetValue("id", out _);
        var hasMethod = root.TryGetValue("method", out _);

        if (hasId && !hasMethod)
        {
            if (root.TryGetValue("error", out _) || root.TryGetValue("result", out _))
            {
                return root.ToObject<JsonRpcResponse>(serializer);
            }

            throw new JsonSerializationException("Response must have either result or error");
        }

        if (hasMethod && !hasId)
        {
            return root.ToObject<JsonRpcNotification>(serializer);
        }

        if (hasMethod && hasId)
        {
            return root.ToObject<JsonRpcRequest>(serializer);
        }

        throw new JsonSerializationException("Invalid JSON-RPC message format");
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value, value.GetType());
    }
}
}
