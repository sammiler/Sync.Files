// File: Settings/SyncFilesSettingsState.cs
// ... (其他 using 语句)
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization; // For DefaultValue attribute

namespace SyncFiles.Core.Settings
{
    [Serializable]
    // [XmlRoot("component")] // 这个 XmlRoot 属性现在由 ProjectWrapper 的 [XmlElement("component")] 处理
    public class SyncFilesSettingsState
    {
        // ... (Mappings, EnvironmentVariablesList, EnvVariables, PythonScriptPath, PythonExecutablePath, WatchEntries, ScriptGroups 保持不变) ...
        [XmlArray("mappings")]
        [XmlArrayItem("Mapping")]
        public List<Models.Mapping> Mappings { get; set; }

        [XmlArray("envVariablesMap")]
        [XmlArrayItem("entry", typeof(EnvironmentVariableEntry))]
        public List<EnvironmentVariableEntry> EnvironmentVariablesList { get; set; }

        [XmlIgnore]
        public Dictionary<string, string> EnvVariables
        {
            get
            {
                var dict = new Dictionary<string, string>();
                if (EnvironmentVariablesList != null)
                {
                    foreach (var item in EnvironmentVariablesList)
                    {
                        if (!string.IsNullOrEmpty(item.Name)) { dict[item.Name] = item.Value; }
                    }
                }
                return dict;
            }
            set
            {
                EnvironmentVariablesList = new List<EnvironmentVariableEntry>();
                if (value != null)
                {
                    foreach (var kvp in value) { EnvironmentVariablesList.Add(new EnvironmentVariableEntry(kvp.Key, kvp.Value)); }
                }
            }
        }

        [XmlElement("pythonScriptPath")]
        [DefaultValue("")] // Helps XmlSerializer to omit if empty
        public string PythonScriptPath { get; set; }

        [XmlElement("pythonExecutablePath")]
        [DefaultValue("")] // Helps XmlSerializer to omit if empty
        public string PythonExecutablePath { get; set; }

        [XmlArray("watchEntries")]
        [XmlArrayItem("WatchEntry")]
        public List<Models.WatchEntry> WatchEntries { get; set; }

        [XmlArray("scriptGroups")]
        [XmlArrayItem("ScriptGroup")]
        public List<Models.ScriptGroup> ScriptGroups { get; set; }


        [XmlAttribute("name")] // 这个属性将由 XmlSerializer 用于 <component name="...">
        public string ComponentName { get; set; }

        public SyncFilesSettingsState()
        {
            Mappings = new System.Collections.Generic.List<Models.Mapping>();
            EnvironmentVariablesList = new System.Collections.Generic.List<EnvironmentVariableEntry>();
            PythonScriptPath = string.Empty;
            PythonExecutablePath = string.Empty;
            WatchEntries = new System.Collections.Generic.List<Models.WatchEntry>();
            ScriptGroups = new System.Collections.Generic.List<Models.ScriptGroup>();
            ComponentName = "SyncFilesConfig"; // Default, matches your XML
        }
    }
}