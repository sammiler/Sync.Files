using System.Collections.Generic;
using YamlDotNet.Serialization;
using SyncFiles.Core.Models; // For Mapping class

namespace SyncFiles.Core.Config.SmartWorkflow
{
    // This class directly represents the structure under a key like "SyncFiles:" in your workflow YAML.
    public class SmartPlatformConfig
    {
        [YamlMember(Alias = "sourceUrl")]
        public string SourceUrl { get; set; }

        [YamlMember(Alias = "targetDir")]
        public string TargetDir { get; set; }

        // This 'Mappings' property directly corresponds to the 'mappings:' list in your YAML
        // under the 'SyncFiles:' key.
        [YamlMember(Alias = "mappings")]
        public List<Mapping> Mappings { get; set; } // Renamed from MappingsList for clarity

        [YamlMember(Alias = "envVariables")]
        public Dictionary<string, string> EnvVariables { get; set; }

        [YamlMember(Alias = "pythonScriptPath")]
        public string PythonScriptPath { get; set; }

        [YamlMember(Alias = "pythonExecutablePath")]
        public string PythonExecutablePath { get; set; }

        [YamlMember(Alias = "watchEntries")]
        public List<SmartWatchEntry> WatchEntries { get; set; }

        public SmartPlatformConfig()
        {
            SourceUrl = string.Empty;
            TargetDir = string.Empty;
            Mappings = new List<Mapping>(); // Initialize
            EnvVariables = new Dictionary<string, string>();
            PythonScriptPath = string.Empty;
            PythonExecutablePath = string.Empty;
            WatchEntries = new List<SmartWatchEntry>();
        }
    }
}