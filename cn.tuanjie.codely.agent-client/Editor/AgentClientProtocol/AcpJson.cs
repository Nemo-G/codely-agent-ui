using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace AgentClientProtocol
{
    public static class AcpJson
    {
        static readonly JsonSerializerSettings settings = CreateSettings();
        static readonly JsonSerializer serializer = JsonSerializer.CreateDefault(settings);

        public static JsonSerializerSettings Settings => settings;
        public static JsonSerializer Serializer => serializer;

        static JsonSerializerSettings CreateSettings()
        {
            var s = new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new CamelCaseNamingStrategy()
                },
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
            };

            // Enums are strings per ACP schema (uses [EnumMember(Value = "...")] where needed)
            s.Converters.Add(new StringEnumConverter());

            // Polymorphic / special converters
            s.Converters.Add(new RequestIdJsonConverter());
            s.Converters.Add(new JsonRpcMessageJsonConverter());
            s.Converters.Add(new SessionUpdateJsonConverter());
            s.Converters.Add(new ContentBlockJsonConverter());
            s.Converters.Add(new ToolCallContentJsonConverter());
            s.Converters.Add(new RequestPermissionOutcomeJsonConverter());
            s.Converters.Add(new McpServerJsonConverter());
            s.Converters.Add(new EmbeddedResourceResourceJsonConverter());

            return s;
        }
    }
}


