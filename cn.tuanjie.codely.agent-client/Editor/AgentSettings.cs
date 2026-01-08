using System.Collections.Generic;

namespace UnityAgentClient
{
    public class AgentSettings
    {
        // Default to codely CLI for out-of-the-box usage.
        public string Command { get; set; } = "codely";
        public string Arguments { get; set; } = "--experimental-acp";
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();
        public bool VerboseLogging { get; set; }
    }
}
