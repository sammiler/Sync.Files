using Microsoft.VisualStudio.Shell.Interop;
using SyncFiles.Core.Config.SmartWorkflow;
using SyncFiles.Core.Management;
using SyncFiles.Core.Models;
using SyncFiles.Core.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
namespace SyncFiles.Core.Services
{
    public class SmartWorkflowService : IDisposable
    {
        private  string _projectBasePath;
        private readonly SyncFilesSettingsManager _settingsManager;
        private  GitHubSyncService _gitHubSyncService;
        private HttpClient _httpClient;
        public event EventHandler WorkflowDownloadPhaseCompleted;
        private SmartPlatformConfig _pendingPlatformConfigFromYaml;
        private bool _isWorkflowSyncInProgress = false; // 标记由本服务发起的同步操作
        private object _lock = new object(); // 用于同步访问 _isWorkflowSyncInProgress 和 _pendingPlatformConfigFromYaml
        public SmartWorkflowService(
            string projectBasePath,
            SyncFilesSettingsManager settingsManager,
            GitHubSyncService gitHubSyncService)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _gitHubSyncService = gitHubSyncService ?? throw new ArgumentNullException(nameof(gitHubSyncService));
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SyncFilesCsharpPlugin-Workflow/1.0");
            _gitHubSyncService.SynchronizationCompleted += OnGitHubSyncServiceCompleted;
        }

        public void UpdateProjectPath(string newProjectPath,GitHubSyncService gitHubSyncService)
        {
            if (this._projectBasePath != newProjectPath && !string.IsNullOrEmpty(newProjectPath))
            {
                this._projectBasePath = newProjectPath;
                _gitHubSyncService = gitHubSyncService ?? throw new ArgumentNullException(nameof(gitHubSyncService));

                Console.WriteLine($"[INFO] GitHubSyncService: Project path updated to '{this._projectBasePath ?? "null"}'.");
                // Reset or reconfigure any internal state that depends on the project path
                // For example, if you cache resolved $PROJECT_DIR$ paths, clear them.
            }
        }
        private string ResolveValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            string resolved = value;
            if (!string.IsNullOrEmpty(_projectBasePath))
            {
                resolved = Regex.Replace(resolved, Regex.Escape("$PROJECT_DIR$"), _projectBasePath.Replace("$", "$$"), RegexOptions.IgnoreCase);
            }
            resolved = Environment.ExpandEnvironmentVariables(resolved);
            return resolved;
        }
        private string GetPlatformKeyFromYaml(Dictionary<string, SmartPlatformConfig> allPlatformsData)
        {
            const string expectedKey = "SyncFiles";
            if (allPlatformsData != null && allPlatformsData.ContainsKey(expectedKey))
            {
                return expectedKey;
            }
            if (allPlatformsData != null && allPlatformsData.Count == 1)
            {
                Console.WriteLine($"[WARN] [WORKFLOW] Expected key '{expectedKey}' not found in YAML, but only one key ('{allPlatformsData.Keys.First()}') exists. Using it.");
                return allPlatformsData.Keys.First();
            }
            Console.WriteLine($"[WARN] [WORKFLOW] Expected key '{expectedKey}' not found in YAML, and no clear fallback. Available keys: {(allPlatformsData == null ? "none" : string.Join(", ", allPlatformsData.Keys))}");
            return null;
        }
        public async Task PrepareWorkflowFromYamlUrlAsync(string yamlUrl, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"[INFO] [WORKFLOW] Phase 1: Starting Smart Workflow from URL: {yamlUrl}");
            string yamlContent;
            try
            {
                Console.WriteLine($"[INFO] [WORKFLOW] Downloading YAML from {yamlUrl}...");
                var response = await _httpClient.GetAsync(yamlUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                yamlContent = await response.Content.ReadAsStringAsync();
                cancellationToken.ThrowIfCancellationRequested();
                Console.WriteLine("[INFO] [WORKFLOW] YAML content downloaded successfully.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[INFO] [WORKFLOW] [CANCELLED] YAML download was cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] [WORKFLOW] Failed to download YAML from '{yamlUrl}': {ex.Message}");
                throw new InvalidOperationException($"Failed to download YAML: {ex.Message}", ex);
            }
            await ProcessYamlContentAsync(yamlContent, cancellationToken);
        }
        public async Task ProcessYamlContentAsync(string yamlContent, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _pendingPlatformConfigFromYaml = null;
                _isWorkflowSyncInProgress = false;
            }
            Console.WriteLine("[INFO] [WORKFLOW] Phase 1: Processing YAML content...");
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            Dictionary<string, SmartPlatformConfig> allPlatformsData;
            try
            {
                allPlatformsData = deserializer.Deserialize<Dictionary<string, SmartPlatformConfig>>(yamlContent);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] [WORKFLOW] Failed to parse YAML content: {ex.Message}");
                throw new FormatException($"Invalid YAML content: {ex.Message}", ex);
            }
            string platformKey = GetPlatformKeyFromYaml(allPlatformsData);
            if (string.IsNullOrEmpty(platformKey) || !allPlatformsData.TryGetValue(platformKey, out var parsedConfig) || parsedConfig == null)
            {
                Console.WriteLine($"[ERROR] [WORKFLOW] No suitable configuration found in YAML for keys like '{platformKey}'.");
                throw new KeyNotFoundException("No suitable configuration found in YAML.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                _pendingPlatformConfigFromYaml = parsedConfig; // Store for Phase 2
            }
            Console.WriteLine($"[INFO] [WORKFLOW] Successfully parsed YAML for key '{platformKey}'.");
            SyncFilesSettingsState currentSettings = _settingsManager.LoadSettings(_projectBasePath);
            bool mappingsUpdated = false;
            if (parsedConfig.Mappings != null && parsedConfig.Mappings.Any())
            {
                currentSettings.Mappings.Clear();
                foreach (var yamlMapping in parsedConfig.Mappings)
                {
                    if (!string.IsNullOrWhiteSpace(yamlMapping.SourceUrl) && !string.IsNullOrWhiteSpace(yamlMapping.TargetPath))
                    {
                        currentSettings.Mappings.Add(new Models.Mapping(
                            ResolveValue(yamlMapping.SourceUrl),
                            ResolveValue(yamlMapping.TargetPath)
                        ));
                        mappingsUpdated = true;
                    }
                }
                if (mappingsUpdated) Console.WriteLine($"[INFO] [WORKFLOW] Mappings updated from YAML 'mappings' list. Count: {currentSettings.Mappings.Count}");
            }
            else if (!string.IsNullOrWhiteSpace(parsedConfig.SourceUrl) && !string.IsNullOrWhiteSpace(parsedConfig.TargetDir))
            {
                currentSettings.Mappings.Clear();
                currentSettings.Mappings.Add(new Models.Mapping(
                    ResolveValue(parsedConfig.SourceUrl),
                    ResolveValue(parsedConfig.TargetDir)
                ));
                mappingsUpdated = true;
                Console.WriteLine("[INFO] [WORKFLOW] Mappings updated from YAML 'sourceUrl'/'targetDir'.");
            }
            else
            {
                Console.WriteLine("[WARN] [WORKFLOW] No explicit 'mappings' list or 'sourceUrl'/'targetDir' found in YAML for mapping updates. Sync will use existing mappings if any.");
            }
            if (mappingsUpdated)
            {
                _settingsManager.SaveSettings(currentSettings, _projectBasePath);
                Console.WriteLine("[INFO] [WORKFLOW] Settings (with updated mappings for sync) saved.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (currentSettings.Mappings == null || !currentSettings.Mappings.Any())
            {
                Console.WriteLine("[WARN] [WORKFLOW] No mappings to process for GitHub sync. Skipping download phase.");
                lock (_lock) { _isWorkflowSyncInProgress = false; }
                WorkflowDownloadPhaseCompleted?.Invoke(this, EventArgs.Empty); // Signal phase 1 completion (even if no download)
                return;
            }
            Console.WriteLine("[INFO] [WORKFLOW] Triggering GitHub file synchronization as part of workflow...");
            lock (_lock)
            {
                _isWorkflowSyncInProgress = true; // Mark that the upcoming sync is part of THIS workflow
            }
            try
            {
                await _gitHubSyncService.SyncAllAsync(currentSettings, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                lock (_lock) { _isWorkflowSyncInProgress = false; } // Reset on cancellation
                Console.WriteLine("[INFO] [WORKFLOW] [CANCELLED] GitHub synchronization (workflow) was cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                lock (_lock) { _isWorkflowSyncInProgress = false; } // Reset on error
                Console.WriteLine($"[ERROR] [WORKFLOW] GitHub synchronization (workflow) failed: {ex.Message}");
                throw;
            }
        }
        private void OnGitHubSyncServiceCompleted(object sender, EventArgs e)
        {
            bool wasThisWorkflowSync;
            lock (_lock)
            {
                wasThisWorkflowSync = _isWorkflowSyncInProgress;
                if (wasThisWorkflowSync)
                {
                    _isWorkflowSyncInProgress = false; // Reset the flag
                }
            }
            if (wasThisWorkflowSync)
            {
                Console.WriteLine("[INFO] [WORKFLOW] GitHub sync (triggered by workflow) has completed. Raising WorkflowDownloadPhaseCompleted event.");
                WorkflowDownloadPhaseCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        public void FinalizeWorkflowConfiguration()
        {
            SmartPlatformConfig yamlConfig;
            lock (_lock) // Ensure thread-safe access and consumption of _pendingPlatformConfigFromYaml
            {
                if (_pendingPlatformConfigFromYaml == null)
                {
                    Console.WriteLine("[WARN] [WORKFLOW] Phase 2 (Finalize) called, but no pending YAML configuration. Aborting.");
                    return;
                }
                yamlConfig = _pendingPlatformConfigFromYaml;
                _pendingPlatformConfigFromYaml = null; // Consume the pending config
            }
            Console.WriteLine("[INFO] [WORKFLOW] Phase 2: Finalizing configuration using stored YAML data.");
            SyncFilesSettingsState currentSettings = _settingsManager.LoadSettings(_projectBasePath);
            if (!string.IsNullOrWhiteSpace(yamlConfig.PythonExecutablePath))
            {
                string resolvedPyExe = ResolveValue(yamlConfig.PythonExecutablePath);
                if (!string.IsNullOrEmpty(resolvedPyExe) && File.Exists(resolvedPyExe))
                {
                    currentSettings.PythonExecutablePath = resolvedPyExe;
                    Console.WriteLine($"[INFO] [WORKFLOW] Python executable path set from YAML: {resolvedPyExe}");
                }
                else
                {
                    Console.WriteLine($"[WARN] [WORKFLOW] Python executable from YAML ('{resolvedPyExe}') not found or path is empty. Retaining existing: '{currentSettings.PythonExecutablePath}'.");
                }
            }
            if (!string.IsNullOrWhiteSpace(yamlConfig.PythonScriptPath))
            {
                string tempPath = ResolveValue(yamlConfig.PythonScriptPath);
                string resolvedPyScriptDir;
                if (Path.IsPathRooted(tempPath))
                {
                    resolvedPyScriptDir = Path.GetFullPath(tempPath);
                }
                else
                {
                    resolvedPyScriptDir = Path.GetFullPath(Path.Combine(_projectBasePath, tempPath));
                }
                if (Directory.Exists(resolvedPyScriptDir))
                {
                    currentSettings.PythonScriptPath = resolvedPyScriptDir;
                    Console.WriteLine($"[INFO] [WORKFLOW] Python script path set from YAML: {resolvedPyScriptDir}");
                }
                else
                {
                    Console.WriteLine($"[WARN] [WORKFLOW] Python script directory from YAML ('{resolvedPyScriptDir}') not found. Retaining existing: '{currentSettings.PythonScriptPath}'.");
                }
            }
            if (yamlConfig.EnvVariables != null)
            {
                currentSettings.EnvironmentVariablesList.Clear();
                currentSettings.EnvironmentVariablesList.Add(new EnvironmentVariableEntry( "PROJECT_DIR", _projectBasePath));
                currentSettings.EnvironmentVariablesList.Add(new EnvironmentVariableEntry("USER_HOME", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
                foreach (var kvp in yamlConfig.EnvVariables)
                {
                    if (kvp.Key == "PROJECT_DIR" || kvp.Key == "USER_HOME" || kvp.Key == "SYSTEM_TYPE") 
                    {
                        continue;
                    }
                    currentSettings.EnvironmentVariablesList.Add(new EnvironmentVariableEntry( kvp.Key, kvp.Value));
                }
                Console.WriteLine($"[INFO] [WORKFLOW] Environment variables updated from YAML. Count: {currentSettings.EnvVariables.Count}");
            }
            if (yamlConfig.WatchEntries != null)
            {
                currentSettings.WatchEntries.Clear();
                foreach (var smartEntry in yamlConfig.WatchEntries)
                {
                    if (!string.IsNullOrWhiteSpace(smartEntry.WatchedPath) && !string.IsNullOrWhiteSpace(smartEntry.OnEventScript))
                    {
                        string resolvedWatched = ResolveValue(smartEntry.WatchedPath);
                        string tempScriptPath = ResolveValue(smartEntry.OnEventScript);
                        string resolvedScript;
                        if (Path.IsPathRooted(tempScriptPath))
                        {
                            resolvedScript = Path.GetFullPath(tempScriptPath);
                        }
                        else
                        {
                            string scriptBase = currentSettings.PythonScriptPath; // Use the PythonScriptPath possibly just set from YAML
                            if (string.IsNullOrWhiteSpace(scriptBase) || !Directory.Exists(scriptBase))
                            {
                                scriptBase = _projectBasePath; // Fallback
                            }
                            resolvedScript = Path.GetFullPath(Path.Combine(scriptBase, tempScriptPath));
                        }
                        if (File.Exists(resolvedScript))
                        {
                            currentSettings.WatchEntries.Add(new Models.WatchEntry(resolvedWatched, resolvedScript));
                        }
                        else
                        {
                            Console.WriteLine($"[WARN] [WORKFLOW] Watch entry script '{resolvedScript}' (for watched path '{resolvedWatched}') does not exist. Skipping this watch entry.");
                        }
                    }
                }
                Console.WriteLine($"[INFO] [WORKFLOW] Watch entries updated from YAML. Count: {currentSettings.WatchEntries.Count}");
            }
            _settingsManager.SaveSettings(currentSettings, _projectBasePath);
            Console.WriteLine("[INFO] [WORKFLOW] Phase 2: All configurations applied and final settings saved.");
        }
        public void UnsubscribeGitHubSyncEvents()
        {
            if (_gitHubSyncService != null)
            {
                _gitHubSyncService.SynchronizationCompleted -= OnGitHubSyncServiceCompleted;
            }
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnsubscribeGitHubSyncEvents(); // Ensure unsubscription
                _httpClient?.Dispose();
                _httpClient = null;
            }
        }
        ~SmartWorkflowService()
        {
            Dispose(false);
        }
    }
}