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
        // Event published when a configured watched path experiences a relevant change.
        // Parameters: (string scriptToExecute, string eventTypeString, string affectedFilePath)
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
                // throw new ArgumentException("Project base path must be a valid existing directory.", nameof(projectBasePath));
                // Or, if projectBasePath can sometimes be conceptual (e.g. solution file path for settings)
                // and individual WatchEntry paths are absolute, this might be relaxed.
                // For now, assume it's a base for resolving relative paths.
                Console.WriteLine($"[WARN] FileSystemWatcherService: Project base path '{projectBasePath}' is null, empty, or does not exist. Relative paths in WatchEntries may not resolve correctly.");
            }
            _projectBasePath = projectBasePath; // Store even if potentially invalid, path resolution logic will handle it
        }

        /// <summary>
        /// Updates the file system watchers based on the provided settings.
        /// Stops existing watchers and starts new ones according to the new configuration.
        /// </summary>
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

                    // Store the resolved entry for event processing
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

            // Determine the actual directory to watch and the filter
            // FileSystemWatcher watches a DIRECTORY.
            if (Directory.Exists(entry.WatchedPath))
            {
                pathToMonitor = entry.WatchedPath;
                // filter remains "*.*" to watch all files/subdirs in this directory
            }
            else if (File.Exists(entry.WatchedPath))
            {
                pathToMonitor = Path.GetDirectoryName(entry.WatchedPath);
                filter = Path.GetFileName(entry.WatchedPath); // Watch only this specific file
            }
            else
            {
                // Path does not exist. Try to watch its parent directory for its creation.
                // This is a best-effort approach.
                pathToMonitor = Path.GetDirectoryName(entry.WatchedPath);
                filter = Path.GetFileName(entry.WatchedPath); // We're interested when this specific name appears.
                if (string.IsNullOrEmpty(pathToMonitor)) // e.g. if WatchedPath was just "file.txt" with no dir
                {
                    pathToMonitor = _projectBasePath ?? Environment.CurrentDirectory; // Fallback
                }
                if (string.IsNullOrEmpty(filter) && string.IsNullOrEmpty(Path.GetExtension(entry.WatchedPath)))
                {
                    // If WatchedPath was "someDir" (intended as dir but not existing), filter for any change.
                    // Or if it was "someDir/", filter is empty.
                    // This means we are interested if "someDir" itself is created/deleted.
                    // FileSystemWatcher on parent will give events for "someDir" appearing.
                    // If WatchedPath itself is a non-existent dir, we watch its parent.
                    // The filter will be the name of the non-existent dir.
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

                // Attach generic event handlers
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

            // Normalize paths for comparison
            string eventFullPath = Path.GetFullPath(e.FullPath);
            string normalizedConfiguredWatchedPath = Path.GetFullPath(configuredWatchedPath);

            bool eventMatchesConfiguredPath = false;

            if (Directory.Exists(normalizedConfiguredWatchedPath))
            {
                // Case 1: Configured path is a directory. Event must be for a file/dir within or the dir itself.
                if (eventFullPath.Equals(normalizedConfiguredWatchedPath, StringComparison.OrdinalIgnoreCase) ||
                    eventFullPath.StartsWith(normalizedConfiguredWatchedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    eventMatchesConfiguredPath = true;
                }
            }
            else
            {
                // Case 2: Configured path is a file (or a non-existent item we're watching for).
                // Event must be for that specific file/item path.
                if (eventFullPath.Equals(normalizedConfiguredWatchedPath, StringComparison.OrdinalIgnoreCase))
                {
                    eventMatchesConfiguredPath = true;
                }
                // For Renamed events, also check if the old path matches
                if (e.ChangeType == WatcherChangeTypes.Renamed && e.OldFullPath != null &&
                    Path.GetFullPath(e.OldFullPath).Equals(normalizedConfiguredWatchedPath, StringComparison.OrdinalIgnoreCase))
                {
                    eventMatchesConfiguredPath = true;
                    // If old path matched, we use old path as the "affected file" for the "delete" part of rename
                    // and new path for "create" part. For simplicity, we can fire two events or one with both.
                    // The current simple WatchedFileChanged event takes one affected path.
                    // Let's prioritize the new path for simplicity or decide which is more relevant.
                    // For now, if OldFullPath matches, it's a relevant event.
                    // We'll use e.FullPath (the new path) as the affected path for script.
                }
            }


            if (eventMatchesConfiguredPath)
            {
                string eventTypeString = e.ChangeType.ToString(); // "Created", "Deleted", "Changed", "Renamed"

                // For Renamed, decide how to represent. Simple way: "Renamed" and pass new path.
                // Or fire two conceptual events: "Deleted" for old path, "Created" for new path.
                // The `WatchedFileChanged` action currently takes one affected path.

                string affectedPathForScript = eventFullPath;
                if (e.ChangeType == WatcherChangeTypes.Deleted &&
                   !eventFullPath.Equals(normalizedConfiguredWatchedPath, StringComparison.OrdinalIgnoreCase) &&
                   normalizedConfiguredWatchedPath.StartsWith(eventFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    // If a parent directory of the configured watch path was deleted,
                    // the event's FullPath might be that parent.
                    // In this case, the "affected file" for the script is still the configured path.
                    affectedPathForScript = normalizedConfiguredWatchedPath;
                }


                Console.WriteLine($"[EVENT] FileSystemWatcherService: Event '{eventTypeString}' on '{e.FullPath}' (Old: '{e.OldFullPath}') matches configured watch for '{configuredWatchedPath}'. Triggering script: '{scriptToExecute}' for affected path: '{affectedPathForScript}'");
                WatchedFileChanged?.Invoke(scriptToExecute, eventTypeString, affectedPathForScript);
            }
            else
            {
                // This event was from a FileSystemWatcher instance but didn't precisely match the
                // specific file/directory criteria for *this* WatchEntry. This can happen if a watcher
                // is on a directory with filter "*.*" for a WatchEntry that was originally a file,
                // and another file in that directory changes.
                // Console.WriteLine($"[DEBUG] FileSystemWatcherService: Event '{e.ChangeType}' on '{e.FullPath}' did NOT precisely match configured path '{configuredWatchedPath}'. Ignored by this handler.");
            }
        }


        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            if (_isDisposed) return;
            Exception ex = e.GetException();
            Console.WriteLine($"[ERROR] FileSystemWatcherService: A watcher error occurred. {(sender as FileSystemWatcher)?.Path}. Error: {ex?.Message}");
            // Potentially try to restart the specific watcher or all watchers if it's a persistent issue.
            // For now, just log. Some errors (like buffer overflow) might require recreating the watcher.
        }

        private string ResolvePath(string pathString, string relativeBasePath)
        {
            if (string.IsNullOrWhiteSpace(pathString)) return string.Empty;

            // 1. Expand $PROJECT_DIR$ if _projectBasePath is available
            if (!string.IsNullOrEmpty(_projectBasePath))
            {
                // Use Regex for case-insensitive replace of $PROJECT_DIR$
                pathString = Regex.Replace(pathString, Regex.Escape("$PROJECT_DIR$"), _projectBasePath.Replace("$", "$$"), RegexOptions.IgnoreCase);
            }

            // 2. Expand other system environment variables
            pathString = Environment.ExpandEnvironmentVariables(pathString);

            // 3. Normalize path separators
            pathString = pathString.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            // 4. Check if absolute or resolve against a base path
            if (Path.IsPathRooted(pathString))
            {
                return Path.GetFullPath(pathString);
            }
            else
            {
                // If path is relative, it could be relative to _projectBasePath or a specific 'relativeBasePath' (like PythonScriptPath)
                string effectiveBasePath = relativeBasePath;
                if (string.IsNullOrEmpty(effectiveBasePath) || !Directory.Exists(effectiveBasePath))
                {
                    // Fallback to _projectBasePath if specific relativeBasePath is invalid or not provided
                    effectiveBasePath = _projectBasePath;
                }
                if (string.IsNullOrEmpty(effectiveBasePath) || !Directory.Exists(effectiveBasePath))
                {
                    // Last fallback if all else fails (though ideally should not happen if projectBasePath is valid)
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
                        // Unsubscribe event handlers to prevent issues during dispose or if they are re-used.
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