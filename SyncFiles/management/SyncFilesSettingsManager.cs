using System;
using System.IO;
using System.Xml; // For XmlWriterSettings
using System.Xml.Serialization;
using SyncFiles.Core.Settings; // 包含 SyncFilesSettingsState, ProjectWrapper
namespace SyncFiles.Core.Management
{
    public class SyncFilesSettingsManager
    {
        private const string ConfigFileName = "syncFilesConfig.xml"; 
        private string GetConfigFilePath(string projectBasePath, bool createPluginDirIfNeeded = true)
        {
            if (string.IsNullOrEmpty(projectBasePath))
            {
                string folderPath = Path.Combine(Path.GetDirectoryName(projectBasePath), ".vs"); // 假设projectBasePath是 .sln 文件路径
                folderPath = Path.Combine(projectBasePath, ".vs");
                if (createPluginDirIfNeeded)
                {
                    Directory.CreateDirectory(folderPath); 
                }
                return Path.Combine(folderPath, ConfigFileName);
            }
            string vsDir = Path.Combine(projectBasePath, ".vs");
            if (createPluginDirIfNeeded)
            {
                Directory.CreateDirectory(vsDir);
            }
            return Path.Combine(vsDir, ConfigFileName);
        }
        public SyncFilesSettingsState LoadSettings(string projectBasePath)
        {
            string filePath = GetConfigFilePath(projectBasePath, false); // Don't create dir if just loading
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[INFO] Configuration file not found at {filePath}. Returning default settings.");
                var defaultState = new SyncFilesSettingsState();
                EnsureDefaultScriptGroup(defaultState);
                return defaultState;
            }
            try
            {
                XmlSerializer projectSerializer = new XmlSerializer(typeof(ProjectWrapper));
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    ProjectWrapper wrapper = (ProjectWrapper)projectSerializer.Deserialize(fs);
                    if (wrapper != null && wrapper.SyncFilesComponent != null)
                    {
                        var state = wrapper.SyncFilesComponent;
                        state.Mappings = state.Mappings ?? new System.Collections.Generic.List<Models.Mapping>();
                        state.EnvironmentVariablesList = state.EnvironmentVariablesList ?? new System.Collections.Generic.List<EnvironmentVariableEntry>();
                        state.WatchEntries = state.WatchEntries ?? new System.Collections.Generic.List<Models.WatchEntry>();
                        state.ScriptGroups = state.ScriptGroups ?? new System.Collections.Generic.List<Models.ScriptGroup>();
                        EnsureDefaultScriptGroup(state);
                        Console.WriteLine($"[INFO] Settings loaded successfully from {filePath}.");
                        return state;
                    }
                    else
                    {
                        Console.WriteLine($"[WARN] Deserialization of {filePath} resulted in null ProjectWrapper or SyncFilesComponent. Returning default settings.");
                        var defaultState = new SyncFilesSettingsState();
                        EnsureDefaultScriptGroup(defaultState);
                        return defaultState;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Error loading settings from {filePath}: {ex.Message}. StackTrace: {ex.StackTrace}. Returning default settings.");
                var defaultState = new SyncFilesSettingsState();
                EnsureDefaultScriptGroup(defaultState);
                return defaultState;
            }
        }
        public void SaveSettings(SyncFilesSettingsState state, string projectBasePath)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var dictionaryToSave = state.EnvVariables; // Access getter to ensure list is populated if needed
            state.EnvVariables = dictionaryToSave; // Access setter to ensure list is correct
            EnsureDefaultScriptGroup(state); // 确保Default组存在并正确
            state.ComponentName = "SyncFilesConfig"; // 确保<component name="...">正确
            ProjectWrapper projectWrapper = new ProjectWrapper
            {
                Version = "1", // Or read from somewhere if dynamic
                SyncFilesComponent = state
            };
            string filePath = GetConfigFilePath(projectBasePath, true); // Create dir if needed
            try
            {
                XmlSerializer projectSerializer = new XmlSerializer(typeof(ProjectWrapper));
                XmlWriterSettings writerSettings = new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    NewLineChars = "\r\n", // Or Environment.NewLine
                    NewLineHandling = NewLineHandling.Replace,
                    OmitXmlDeclaration = false //  <project version="4"> 之前通常有 <?xml version="1.0" encoding="UTF-8"?>
                };
                XmlSerializerNamespaces ns = new XmlSerializerNamespaces();
                ns.Add("", ""); // 添加一个空的前缀和命名空间
                using (XmlWriter xmlWriter = XmlWriter.Create(filePath, writerSettings))
                {
                    projectSerializer.Serialize(xmlWriter, projectWrapper, ns);
                }
                Console.WriteLine($"[INFO] Settings saved successfully to {filePath}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Error saving settings to {filePath}: {ex.Message}. StackTrace: {ex.StackTrace}");
            }
        }
        private void EnsureDefaultScriptGroup(SyncFilesSettingsState state)
        {
            if (state.ScriptGroups == null)
            {
                state.ScriptGroups = new System.Collections.Generic.List<Models.ScriptGroup>();
            }
            bool defaultGroupExists = false;
            foreach (var group in state.ScriptGroups)
            {
                if (group.Id == Models.ScriptGroup.DefaultGroupId)
                {
                    defaultGroupExists = true;
                    if (group.Name != Models.ScriptGroup.DefaultGroupName)
                    {
                        group.Name = Models.ScriptGroup.DefaultGroupName;
                    }
                    break;
                }
            }
            if (!defaultGroupExists)
            {
                state.ScriptGroups.Insert(0, new Models.ScriptGroup(Models.ScriptGroup.DefaultGroupId, Models.ScriptGroup.DefaultGroupName));
            }
        }
    }
}