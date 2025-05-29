using SyncFiles.Core.Models;       // For WatchEntry
using SyncFiles.Core.Settings;     // For SyncFilesSettingsState
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Permissions; // For FileSystemWatcher permission demands (though often implicit)
using System.Text.RegularExpressions;
namespace SyncFiles.Core.Services
{
    public class FileSystemEventArgsWrapper
    {
        public string FullPath { get; }
        public string OldFullPath { get; } // Only for Renamed events
        public WatcherChangeTypes ChangeType { get; }
        public string Name { get; } // File or directory name
        public string OldName { get; } // Only for Renamed events
        public FileSystemEventArgsWrapper(FileSystemEventArgs e)
        {
            FullPath = e.FullPath;
            ChangeType = e.ChangeType;
            Name = e.Name;
            OldFullPath = null;
            OldName = null;
        }
        public FileSystemEventArgsWrapper(RenamedEventArgs e)
        {
            FullPath = e.FullPath;
            ChangeType = e.ChangeType; // Will be WatcherChangeTypes.Renamed
            Name = e.Name;
            OldFullPath = e.OldFullPath;
            OldName = e.OldName;
        }
    }
    public class FileSystemWatcherService : IDisposable
    {
        public event Action<string, string, string> WatchedFileChanged;
        private readonly string _projectBasePath;
        private List<FileSystemWatcher> _activeWatchers = new List<FileSystemWatcher>();
        private List<WatchEntry> _currentWatchEntries = new List<WatchEntry>(); // Stores the resolved watch entries
        private bool _isDisposed = false;
        private object _lock = new object(); // For thread safety when modifying watchers
        public FileSystemWatcherService(string projectBasePath)
        {
            if (string.IsNullOrEmpty(projectBasePath) || !Directory.Exists(projectBasePath))
            {
                Console.WriteLine($"[WARN] FileSystemWatcherService: Project base path '{projectBasePath}' is null, empty, or does not exist. Relative paths in WatchEntries may not resolve correctly.");
            }
            _projectBasePath = projectBasePath; // Store even if potentially invalid, path resolution logic will handle it
        }
        public void UpdateWatchers(SyncFilesSettingsState settings)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(FileSystemWatcherService));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            lock (_lock)
            {
                StopAllWatchers(); // Dispose old watchers
                _currentWatchEntries.Clear();
                if (settings.WatchEntries == null || !settings.WatchEntries.Any())
                {
                    Console.WriteLine("[INFO] FileSystemWatcherService: No watch entries configured.");
                    return;
                }
                Console.WriteLine($"[INFO] FileSystemWatcherService: Configuring {settings.WatchEntries.Count} watch entries.");
                foreach (var entry in settings.WatchEntries)
                {
                    if (string.IsNullOrWhiteSpace(entry.WatchedPath) || string.IsNullOrWhiteSpace(entry.OnEventScript))
                    {
                        Console.WriteLine($"[WARN] FileSystemWatcherService: Skipping invalid WatchEntry (empty path or script): Watched='{entry.WatchedPath}', Script='{entry.OnEventScript}'");
                        continue;
                    }
                    string resolvedWatchedPath = ResolvePath(entry.WatchedPath, _projectBasePath);
                    string resolvedScriptPath = ResolvePath(entry.OnEventScript, settings.PythonScriptPath); // Scripts are relative to PythonScriptPath or project if absolute
                    if (string.IsNullOrEmpty(resolvedWatchedPath))
                    {
                        Console.WriteLine($"[WARN] FileSystemWatcherService: Could not resolve watched path '{entry.WatchedPath}'. Skipping entry.");
                        continue;
                    }
                    if (string.IsNullOrEmpty(resolvedScriptPath) || !File.Exists(resolvedScriptPath)) // Script must exist to be valid
                    {
                        Console.WriteLine($"[WARN] FileSystemWatcherService: Script '{entry.OnEventScript}' (resolved to '{resolvedScriptPath}') does not exist or path is invalid. Skipping entry for watched path '{resolvedWatchedPath}'.");
                        continue;
                    }
                    var processedEntry = new WatchEntry(resolvedWatchedPath, resolvedScriptPath);
                    _currentWatchEntries.Add(processedEntry);
                    SetupWatcherForEntry(processedEntry);
                }
                Console.WriteLine($"[INFO] FileSystemWatcherService: Finished configuring watchers. {_activeWatchers.Count} active FileSystemWatcher instances.");
            }
        }
        private void SetupWatcherForEntry(WatchEntry entry)
        {
            string pathToMonitor;
            string filter = "*.*"; // Default to all files in a directory
            if (Directory.Exists(entry.WatchedPath))
            {
                pathToMonitor = entry.WatchedPath;
            }
            else if (File.Exists(entry.WatchedPath))
            {
                pathToMonitor = Path.GetDirectoryName(entry.WatchedPath);
                filter = Path.GetFileName(entry.WatchedPath); // Watch only this specific file
            }
            else
            {
                pathToMonitor = Path.GetDirectoryName(entry.WatchedPath);
                filter = Path.GetFileName(entry.WatchedPath); // We're interested when this specific name appears.
                if (string.IsNullOrEmpty(pathToMonitor)) // e.g. if WatchedPath was just "file.txt" with no dir
                {
                    pathToMonitor = _projectBasePath ?? Environment.CurrentDirectory; // Fallback
                }
                if (string.IsNullOrEmpty(filter) && string.IsNullOrEmpty(Path.GetExtension(entry.WatchedPath)))
                {
                }
            }
            if (string.IsNullOrEmpty(pathToMonitor) || !Directory.Exists(pathToMonitor))
            {
                Console.WriteLine($"[WARN] FileSystemWatcherService: Cannot monitor '{entry.WatchedPath}'. The directory to watch ('{pathToMonitor}') is invalid or does not exist.");
                return;
            }
            try
            {
                FileSystemWatcher watcher = new FileSystemWatcher(pathToMonitor)
                {
                    Filter = filter,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = Directory.Exists(entry.WatchedPath) // Only include subdirs if WatchedPath IS an existing directory
                };
                watcher.Changed += (s, e) => OnFileSystemEvent(new FileSystemEventArgsWrapper(e), entry.WatchedPath, entry.OnEventScript);
                watcher.Created += (s, e) => OnFileSystemEvent(new FileSystemEventArgsWrapper(e), entry.WatchedPath, entry.OnEventScript);
                watcher.Deleted += (s, e) => OnFileSystemEvent(new FileSystemEventArgsWrapper(e), entry.WatchedPath, entry.OnEventScript);
                watcher.Renamed += (s, e) => OnFileSystemEvent(new FileSystemEventArgsWrapper(e), entry.WatchedPath, entry.OnEventScript);
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                _activeWatchers.Add(watcher);
                Console.WriteLine($"[INFO] FileSystemWatcherService: Watching '{pathToMonitor}' (filter: '{filter}') for configured path '{entry.WatchedPath}'. Script: '{entry.OnEventScript}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] FileSystemWatcherService: Failed to create watcher for '{pathToMonitor}' (for configured path '{entry.WatchedPath}'): {ex.Message}");
            }
        }
        private void OnFileSystemEvent(FileSystemEventArgsWrapper e, string configuredWatchedPath, string scriptToExecute)
        {
            if (_isDisposed) return;
            string eventFullPath = Path.GetFullPath(e.FullPath);
            string normalizedConfiguredWatchedPath = Path.GetFullPath(configuredWatchedPath);
            bool eventMatchesConfiguredPath = false;
            if (Directory.Exists(normalizedConfiguredWatchedPath))
            {
                if (eventFullPath.Equals(normalizedConfiguredWatchedPath, StringComparison.OrdinalIgnoreCase) ||
                    eventFullPath.StartsWith(normalizedConfiguredWatchedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    eventMatchesConfiguredPath = true;
                }
            }
            else
            {
                if (eventFullPath.Equals(normalizedConfiguredWatchedPath, StringComparison.OrdinalIgnoreCase))
                {
                    eventMatchesConfiguredPath = true;
                }
                if (e.ChangeType == WatcherChangeTypes.Renamed && e.OldFullPath != null &&
                    Path.GetFullPath(e.OldFullPath).Equals(normalizedConfiguredWatchedPath, StringComparison.OrdinalIgnoreCase))
                {
                    eventMatchesConfiguredPath = true;
                }
            }
            if (eventMatchesConfiguredPath)
            {
                string eventTypeString = e.ChangeType.ToString(); // "Created", "Deleted", "Changed", "Renamed"
                string affectedPathForScript = eventFullPath;
                if (e.ChangeType == WatcherChangeTypes.Deleted &&
                   !eventFullPath.Equals(normalizedConfiguredWatchedPath, StringComparison.OrdinalIgnoreCase) &&
                   normalizedConfiguredWatchedPath.StartsWith(eventFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    affectedPathForScript = normalizedConfiguredWatchedPath;
                }
                Console.WriteLine($"[EVENT] FileSystemWatcherService: Event '{eventTypeString}' on '{e.FullPath}' (Old: '{e.OldFullPath}') matches configured watch for '{configuredWatchedPath}'. Triggering script: '{scriptToExecute}' for affected path: '{affectedPathForScript}'");
                WatchedFileChanged?.Invoke(scriptToExecute, eventTypeString, affectedPathForScript);
            }
            else
            {
            }
        }
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            if (_isDisposed) return;
            Exception ex = e.GetException();
            Console.WriteLine($"[ERROR] FileSystemWatcherService: A watcher error occurred. {(sender as FileSystemWatcher)?.Path}. Error: {ex?.Message}");
        }
        private string ResolvePath(string pathString, string relativeBasePath)
        {
            if (string.IsNullOrWhiteSpace(pathString)) return string.Empty;
            if (!string.IsNullOrEmpty(_projectBasePath))
            {
                pathString = Regex.Replace(pathString, Regex.Escape("$PROJECT_DIR$"), _projectBasePath.Replace("$", "$$"), RegexOptions.IgnoreCase);
            }
            pathString = Environment.ExpandEnvironmentVariables(pathString);
            pathString = pathString.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(pathString))
            {
                return Path.GetFullPath(pathString);
            }
            else
            {
                string effectiveBasePath = relativeBasePath;
                if (string.IsNullOrEmpty(effectiveBasePath) || !Directory.Exists(effectiveBasePath))
                {
                    effectiveBasePath = _projectBasePath;
                }
                if (string.IsNullOrEmpty(effectiveBasePath) || !Directory.Exists(effectiveBasePath))
                {
                    Console.WriteLine($"[WARN] Could not determine a valid base path for relative path '{pathString}'. Using current directory as base.");
                    effectiveBasePath = Environment.CurrentDirectory;
                }
                return Path.GetFullPath(Path.Combine(effectiveBasePath, pathString));
            }
        }
        private void StopAllWatchers()
        {
            lock (_lock) // Ensure thread safety
            {
                if (_activeWatchers != null)
                {
                    Console.WriteLine($"[INFO] FileSystemWatcherService: Stopping {_activeWatchers.Count} active watchers.");
                    foreach (var watcher in _activeWatchers)
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Changed -= (s, e) => OnFileSystemEvent(new FileSystemEventArgsWrapper(e), null, null); // Dummy args, handler checks for disposed
                        watcher.Created -= (s, e) => OnFileSystemEvent(new FileSystemEventArgsWrapper(e), null, null);
                        watcher.Deleted -= (s, e) => OnFileSystemEvent(new FileSystemEventArgsWrapper(e), null, null);
                        watcher.Renamed -= (s, e) => OnFileSystemEvent(new FileSystemEventArgsWrapper(e), null, null);
                        watcher.Error -= OnWatcherError;
                        watcher.Dispose();
                    }
                    _activeWatchers.Clear();
                }
            }
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                lock (_lock)
                {
                    StopAllWatchers();
                    _currentWatchEntries?.Clear();
                }
            }
            _isDisposed = true;
        }
        ~FileSystemWatcherService()
        {
            Dispose(false);
        }
    }
}