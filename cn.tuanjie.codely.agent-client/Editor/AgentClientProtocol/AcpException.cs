using System;
using Newtonsoft.Json.Linq;

namespace AgentClientProtocol
{
public class AcpException : Exception
{
    public JToken ErrorData { get; }
    public int Code { get; }

    public AcpException(string message, JToken data, int code, Exception innerException = null)
        : base(message, innerException)
    {
        ErrorData = data;
        Code = code;
    }

    public override string ToString()
    {
        return $"{Message}: {ErrorData}";
    }

    internal static void ThrowIfParamIsNull(JToken param)
    {
        if (param == null || param.Type == JTokenType.Null)
        {
            throw new AcpException("Params is null", null, (int)JsonRpcErrorCode.InvalidParams);
        }
    }
}
}
