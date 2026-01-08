using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public abstract record RequestPermissionOutcome
{
    public abstract string Outcome { get; }
}

public record CancelledRequestPermissionOutcome : RequestPermissionOutcome
{
    public override string Outcome => "cancelled";
}

public record SelectedRequestPermissionOutcome : RequestPermissionOutcome
{
    public override string Outcome => "selected";
    public string OptionId { get; init; }
}

public sealed class RequestPermissionOutcomeJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(RequestPermissionOutcome);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        var obj = JObject.Load(reader);

        if (obj.TryGetValue("optionId", out _))
        {
            return obj.ToObject<SelectedRequestPermissionOutcome>(serializer);
        }

        return obj.ToObject<CancelledRequestPermissionOutcome>(serializer);
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value, value.GetType());
    }
}
}
