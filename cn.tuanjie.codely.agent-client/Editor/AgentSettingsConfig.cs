using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnityAgentClient
{
    internal static class AgentSettingsConfig
    {
        const string ConfigFileName = "UnityAgentClientSettings.json";
        const string DefaultCodelyArguments = "--experimental-acp";

        static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
        };

        public static string ConfigFilePath => Path.Combine(GetUserSettingsPath(), ConfigFileName);

        static string GetUserSettingsPath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var userSettingsPath = Path.Combine(projectRoot, "UserSettings");

            if (!Directory.Exists(userSettingsPath))
            {
                Directory.CreateDirectory(userSettingsPath);
            }

            return userSettingsPath;
        }

        public static AgentSettings Load()
        {
            try
            {
                // Always ensure a config file exists so users never end up with "null config" on first launch.
                EnsureExists();

                if (File.Exists(ConfigFilePath))
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    var settings = JsonConvert.DeserializeObject<AgentSettings>(json, JsonSettings);

                    if (settings == null)
                    {
                        Logger.LogError("Failed to load config: deserialized to null. Falling back to defaults.");
                        return new AgentSettings();
                    }

                    // Be defensive against nulls / partially-written JSON.
                    settings.EnvironmentVariables ??= new Dictionary<string, string>();

                    var defaultSettings = new AgentSettings();
                    var migrated = false;

                    if (string.IsNullOrWhiteSpace(settings.Command))
                    {
                        settings.Command = defaultSettings.Command;
                        migrated = true;
                    }

                    // If args are empty, pick a safe default based on the chosen command.
                    // This is important because some agents (e.g. codely, gemini) only speak ACP when a flag is provided.
                    if (string.IsNullOrWhiteSpace(settings.Arguments))
                    {
                        if (IsCodelyCommand(settings.Command))
                        {
                            settings.Arguments = DefaultCodelyArguments;
                            migrated = true;
                        }
                        else
                        {
                            settings.Arguments = string.Empty;
                        }
                    }

                    // Persist one-time migration so the config file becomes self-explanatory for users.
                    if (migrated)
                    {
                        Save(settings);
                    }

                    return settings;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to load config: {ex.Message}");
            }

            return new AgentSettings();
        }

        static bool IsCodelyCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;

            try
            {
                // Handles "codely", "codely.cmd", and full paths like "C:\\Program Files\\nodejs\\codely.cmd".
                var name = Path.GetFileNameWithoutExtension(command);
                return string.Equals(name, "codely", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static void Save(AgentSettings config)
        {
            try
            {
                string json = JsonConvert.SerializeObject(config, JsonSettings);
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to save config: {ex.Message}");
            }
        }

        public static void EnsureExists()
        {
            if (File.Exists(ConfigFilePath)) return;
            Save(new AgentSettings());
        }

        public static void OpenConfigFile()
        {
            EnsureExists();
            UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(ConfigFilePath, 1);
        }
    }
}


