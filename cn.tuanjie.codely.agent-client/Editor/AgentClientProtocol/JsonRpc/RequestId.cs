using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace AgentClientProtocol
{
[StructLayout(LayoutKind.Auto)]
public readonly struct RequestId : IEquatable<RequestId>
{
    readonly RequestIdType type;
    readonly long numberValue;
    readonly string? stringValue;

    public RequestIdType Type => type;
    public bool IsValid => type is not RequestIdType.Invalid;

    public RequestId(long value)
    {
        type = RequestIdType.Number;
        numberValue = value;
        stringValue = null;
    }

    public RequestId(string value)
    {
        type = RequestIdType.String;
        numberValue = 0;
        stringValue = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long AsNumber()
    {
        if (type is not RequestIdType.Number) ThrowTypeIsNot("number");
        return numberValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string AsString()
    {
        if (type is not RequestIdType.String) ThrowTypeIsNot("string");
        return stringValue!;
    }

    static void ThrowTypeIsNot(string expected)
    {
        throw new InvalidOperationException($"RequestId type is not a {expected}");
    }

    public override string ToString()
    {
        return type switch
        {
            RequestIdType.Invalid => "",
            RequestIdType.Number => numberValue.ToString(),
            RequestIdType.String => stringValue!,
            _ => "",
        };
    }

    public bool Equals(RequestId other)
    {
        if (type != other.type) return false;

        return type switch
        {
            RequestIdType.Number => numberValue == other.numberValue,
            RequestIdType.String => stringValue == other.stringValue,
            _ => false,
        };
    }

    public override bool Equals(object? obj)
    {
        return obj is RequestId id && Equals(id);
    }

    public override int GetHashCode()
    {
        return type switch
        {
            RequestIdType.Number => HashCode.Combine(0, numberValue),
            RequestIdType.String => HashCode.Combine(1, stringValue),
            _ => 0,
        };
    }

    public static bool operator ==(RequestId left, RequestId right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(RequestId left, RequestId right)
    {
        return !(left == right);
    }

    public static implicit operator RequestId(long value)
    {
        return new RequestId(value);
    }

    public static implicit operator RequestId(string value)
    {
        return new RequestId(value);
    }
}

public enum RequestIdType : byte
{
    Invalid,
    Number,
    String
}

public sealed class RequestIdJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(RequestId);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        switch (reader.TokenType)
        {
            case JsonToken.Integer:
                return new RequestId(Convert.ToInt64(reader.Value));
            case JsonToken.String:
                return new RequestId((string)reader.Value);
            case JsonToken.Null:
                return default(RequestId);
            default:
                throw new JsonSerializationException("Invalid type for RequestId. Expected integer or string.");
        }
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        var id = (RequestId)value;

        if (!id.IsValid)
        {
            writer.WriteNull();
            return;
        }

        switch (id.Type)
        {
            case RequestIdType.Number:
                writer.WriteValue(id.AsNumber());
                break;
            case RequestIdType.String:
                writer.WriteValue(id.AsString());
                break;
            default:
                writer.WriteNull();
                break;
        }
    }
}
}
