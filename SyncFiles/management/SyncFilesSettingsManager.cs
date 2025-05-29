using System;
using System.IO;
using System.Xml; // For XmlWriterSettings
using System.Xml.Serialization;
using SyncFiles.Core.Settings; // 包含 SyncFilesSettingsState, ProjectWrapper

namespace SyncFiles.Core.Management
{
    public class SyncFilesSettingsManager
    {
        private const string ConfigFileName = "syncFilesConfig.xml"; // 与IntelliJ插件一致

        private string GetConfigFilePath(string projectBasePath, bool createPluginDirIfNeeded = true)
        {
            if (string.IsNullOrEmpty(projectBasePath))
            {
                // 对于VS扩展，可以考虑存储在用户设置或解决方案的.vs目录
                // 这里为了模拟IntelliJ的行为，我们尝试项目下的.idea目录
                // 但VS扩展不应该直接写.idea目录，除非是与Rider等共享配置
                // 更好的VS原生位置是 %APPDATA%\YourExtensionName\ 或 .vs\[SolutionName]\YourExtensionName
                // 为简单起见，并尝试匹配你的XML文件位置，我们先用.idea
                // 【重要】在实际VS扩展中，这个路径需要根据VS的推荐做法调整。
                // 例如，如果这是项目特定设置，可能存储在项目文件旁边或.vs子目录。
                // 这里假设我们是为了能读取你提供的 syncFilesConfig.xml 而放在 .idea
                string ideaFolderPath = Path.Combine(Path.GetDirectoryName(projectBasePath), ".idea"); // 假设projectBasePath是 .sln 文件路径
                                                                                                       // 或者如果是项目文件夹路径 projectBasePath 本身。
                                                                                                       // 如果 projectBasePath 是项目文件夹的路径:
                                                                                                       // string ideaFolderPath = Path.Combine(projectBasePath, ".idea");

                // 为了通用性，先假设 projectBasePath 就是项目根目录
                ideaFolderPath = Path.Combine(projectBasePath, ".idea");


                if (createPluginDirIfNeeded)
                {
                    Directory.CreateDirectory(ideaFolderPath); // 确保.idea目录存在
                }
                return Path.Combine(ideaFolderPath, ConfigFileName);
            }

            // 如果有项目路径，则使用.idea子目录
            string ideaDir = Path.Combine(projectBasePath, ".idea");
            if (createPluginDirIfNeeded)
            {
                Directory.CreateDirectory(ideaDir);
            }
            return Path.Combine(ideaDir, ConfigFileName);
        }

        public SyncFilesSettingsState LoadSettings(string projectBasePath)
        {
            string filePath = GetConfigFilePath(projectBasePath, false); // Don't create dir if just loading
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[INFO] Configuration file not found at {filePath}. Returning default settings.");
                var defaultState = new SyncFilesSettingsState();
                // 确保默认组存在于新创建的state中
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
                        // 确保 SyncFilesComponent 内部的集合不是 null
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

            // 确保 EnvVariables 字典被转换回 EnvironmentVariablesList 以进行正确的 XML 序列化
            // (这应该由 SyncFilesSettingsState 的 EnvVariables setter 处理，但再次确认无妨)
            var dictionaryToSave = state.EnvVariables; // Access getter to ensure list is populated if needed
            state.EnvVariables = dictionaryToSave; // Access setter to ensure list is correct

            EnsureDefaultScriptGroup(state); // 确保Default组存在并正确
            state.ComponentName = "SyncFilesConfig"; // 确保<component name="...">正确

            ProjectWrapper projectWrapper = new ProjectWrapper
            {
                Version = "4", // Or read from somewhere if dynamic
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

                // 为了移除 xmlns:xsi 和 xmlns:xsd 命名空间，这通常是 XmlSerializer 的默认行为
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
                    // 确保默认组的名称也正确
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