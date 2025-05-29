using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression; // For ZipFile
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions; // For URL parsing if needed
using System.Threading;
using System.Threading.Tasks;
using SyncFiles.Core.Models;    // For Mapping
using SyncFiles.Core.Settings;  // For SyncFilesSettingsState

namespace SyncFiles.Core.Services
{
    public class GitHubSyncService : IDisposable
    {
        private readonly string _projectBasePath;
        private readonly HttpClient _httpClient;

        // Event to notify when all synchronization operations are complete (similar to FileDownloadFinishedNotifier)
        public event EventHandler SynchronizationCompleted;
        // Event to notify progress updates
        public event Action<string> ProgressReported;


        public GitHubSyncService(string projectBasePath)
        {
            if (string.IsNullOrEmpty(projectBasePath))
            {
                throw new ArgumentNullException(nameof(projectBasePath), "Project base path cannot be null or empty.");
            }
            _projectBasePath = Path.GetFullPath(projectBasePath); // Ensure it's an absolute path

            _httpClient = new HttpClient();
            // GitHub API prefers a User-Agent header
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SyncFilesCsharpPlugin/1.0");
        }

        /// <summary>
        /// Resolves a given path string. If relative, it's resolved against the project base path.
        /// Expands common environment variables like $PROJECT_DIR$ and system variables.
        /// </summary>
        private string ResolvePath(string pathString)
        {
            if (string.IsNullOrWhiteSpace(pathString))
            {
                return string.Empty; // Or throw, depending on how critical an empty path is
            }

            // Replace $PROJECT_DIR$ first (case-insensitive for robustness)
            pathString = Regex.Replace(pathString, Regex.Escape("$PROJECT_DIR$"), _projectBasePath, RegexOptions.IgnoreCase);

            // Expand system environment variables (e.g., %USERPROFILE% on Windows)

            // Normalize path separators for the current OS before checking if rooted or combining
            pathString = pathString.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(pathString))
            {
                return Path.GetFullPath(pathString); // Normalizes and returns absolute
            }
            else
            {
                return Path.GetFullPath(Path.Combine(_projectBasePath, pathString));
            }
        }

        protected virtual void OnProgressReported(string message)
        {
            ProgressReported?.Invoke(message);
            Console.WriteLine($"[PROGRESS] {message}"); // Placeholder for real logging/UI update
        }

        protected virtual void OnSynchronizationCompleted()
        {
            SynchronizationCompleted?.Invoke(this, EventArgs.Empty);
        }

        public async Task SyncAllAsync(SyncFilesSettingsState settings, CancellationToken cancellationToken = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (settings.Mappings == null || !settings.Mappings.Any())
            {
                OnProgressReported("No mappings configured. Nothing to sync.");
                OnSynchronizationCompleted(); // Still fire completed event
                return;
            }

            OnProgressReported($"Starting synchronization for {settings.Mappings.Count} mapping(s)...");
            int count = 0;
            int total = settings.Mappings.Count;

            foreach (var mapping in settings.Mappings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                count++;
                string shortUrl = mapping.SourceUrl.Length > 60 ? mapping.SourceUrl.Substring(0, 57) + "..." : mapping.SourceUrl;
                OnProgressReported($"Syncing ({count}/{total}): {shortUrl}");

                if (string.IsNullOrWhiteSpace(mapping.SourceUrl) || string.IsNullOrWhiteSpace(mapping.TargetPath))
                {
                    OnProgressReported($"Skipping invalid mapping: Source or Target is empty (Source: '{mapping.SourceUrl}', Target: '{mapping.TargetPath}')");
                    continue;
                }

                string targetFullPath = ResolvePath(mapping.TargetPath);
                if (string.IsNullOrEmpty(targetFullPath))
                {
                    OnProgressReported($"Skipping mapping due to unresolvable target path: {mapping.TargetPath}");
                    continue;
                }


                try
                {
                    // GitHub raw content URLs or direct file links (blob)
                    if (mapping.SourceUrl.Contains("raw.githubusercontent.com") ||
                        mapping.SourceUrl.Contains("/blob/"))
                    {
                        string rawUrl = mapping.SourceUrl.Replace("/blob/", "/raw/"); // Ensure it's a raw link
                        await FetchFileAsync(rawUrl, targetFullPath, cancellationToken);
                    }
                    // GitHub directory links
                    else if (mapping.SourceUrl.Contains("/tree/"))
                    {
                        await FetchDirectoryAsync(mapping.SourceUrl, targetFullPath, cancellationToken);
                    }
                    else
                    {
                        OnProgressReported($"Unsupported URL format: {mapping.SourceUrl}");
                    }
                }
                catch (OperationCanceledException)
                {
                    OnProgressReported("Synchronization cancelled by user.");
                    throw; // Re-throw to stop further processing
                }
                catch (Exception ex)
                {
                    OnProgressReported($"[ERROR] Failed to sync {mapping.SourceUrl}: {ex.Message}");
                    // Decide if one error should stop all, or continue with other mappings
                    // For now, we log and continue.
                }
            }

            OnProgressReported("Synchronization process finished.");
            OnSynchronizationCompleted();
            // TODO: Trigger VFS Refresh if in an IDE context
        }

        private async Task FetchFileAsync(string url, string targetFilePath, CancellationToken cancellationToken)
        {
            OnProgressReported($"Downloading file: {Path.GetFileName(targetFilePath)} from {url}");

            HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Failed to fetch file. Status: {response.StatusCode} ({response.ReasonPhrase}) from URL: {url}. Response: {errorContent}");
            }

            // Ensure target directory exists
            string targetDirectory = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            using (Stream contentStream = await response.Content.ReadAsStreamAsync())
            using (FileStream fileStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 8192, useAsync: true))
            {
                await contentStream.CopyToAsync(fileStream, 8192, cancellationToken);
            }
            OnProgressReported($"File saved: {targetFilePath}");
        }

        private async Task FetchDirectoryAsync(string githubRepoUrl, string targetDirectoryPath, CancellationToken cancellationToken)
        {
            OnProgressReported($"Fetching directory from: {githubRepoUrl}");

            // 1. Parse GitHub URL (user, repo, branch, subPath)
            // Example: "https://github.com/user/repo/tree/main/path/to/dir"
            Uri uri = new Uri(githubRepoUrl);
            string[] segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length < 4 || segments[2].ToLowerInvariant() != "tree")
            {
                throw new ArgumentException("Invalid GitHub directory URL format. Must contain '/tree/'. URL: " + githubRepoUrl);
            }

            string user = segments[0];
            string repoName = segments[1];
            string branch = segments[3];
            string subPathInRepo = segments.Length > 4 ? string.Join("/", segments.Skip(4)) : "";
            subPathInRepo = subPathInRepo.Trim('/'); // Remove leading/trailing slashes

            // 2. Construct GitHub API URL for zipball
            string zipApiUrl = $"https://api.github.com/repos/{user}/{repoName}/zipball/{branch}";
            OnProgressReported($"Requesting ZIP from API: {zipApiUrl}");

            // 3. Download ZIP archive
            HttpResponseMessage response = await _httpClient.GetAsync(zipApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Failed to fetch repository ZIP from API. Status: {response.StatusCode} ({response.ReasonPhrase}) from URL: {zipApiUrl}. Response: {errorContent}");
            }

            // 4. Save to a temporary file and extract
            string tempZipFilePath = Path.GetTempFileName();
            // Create a unique temporary directory for extraction
            string tempExtractDirPath = Path.Combine(Path.GetTempPath(), "SyncFiles_" + Guid.NewGuid().ToString("N"));

            try
            {
                OnProgressReported("Downloading repository archive...");
                using (Stream zipStream = await response.Content.ReadAsStreamAsync())
                using (FileStream fs = new FileStream(tempZipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    await zipStream.CopyToAsync(fs, 8192, cancellationToken);
                }
                OnProgressReported($"Archive saved to: {tempZipFilePath}");

                cancellationToken.ThrowIfCancellationRequested();
                OnProgressReported($"Extracting archive to: {tempExtractDirPath}...");
                Directory.CreateDirectory(tempExtractDirPath); // Ensure extraction path exists
                ZipFile.ExtractToDirectory(tempZipFilePath, tempExtractDirPath); // Extracts content

                // GitHub zips contain a root folder, usually named 'user-repo-commitsha' or 'user-repo-branchname-commitsha'
                string[] extractedDirs = Directory.GetDirectories(tempExtractDirPath);
                if (extractedDirs.Length == 0)
                {
                    throw new IOException($"No root directory found in the extracted ZIP content at {tempExtractDirPath}. The archive might be empty or malformed.");
                }
                string extractedRepoRootPath = extractedDirs[0]; // Assume the first directory is the repo root
                OnProgressReported($"Detected extracted repository root: {extractedRepoRootPath}");

                string sourcePathInExtractedZip = extractedRepoRootPath;
                if (!string.IsNullOrEmpty(subPathInRepo))
                {
                    sourcePathInExtractedZip = Path.Combine(extractedRepoRootPath, subPathInRepo.Replace('/', Path.DirectorySeparatorChar));
                }

                if (!Directory.Exists(sourcePathInExtractedZip))
                {
                    // Log available directories for debugging
                    string availableContentMessage = "Available content in " + extractedRepoRootPath + ":\n" +
                        string.Join("\n", Directory.GetFileSystemEntries(extractedRepoRootPath).Select(e => "  " + Path.GetFileName(e)));
                    OnProgressReported(availableContentMessage);
                    throw new DirectoryNotFoundException($"SubPath '{subPathInRepo}' (resolved to '{sourcePathInExtractedZip}') does not exist within the downloaded repository content.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                OnProgressReported($"Merging files from '{Path.GetFileName(sourcePathInExtractedZip)}' to '{targetDirectoryPath}'...");
                await MergeDirectoryAsync(sourcePathInExtractedZip, targetDirectoryPath, cancellationToken);

                OnProgressReported($"Directory synced: {targetDirectoryPath}");
            }
            finally
            {
                // 5. Cleanup temporary files and directories
                if (File.Exists(tempZipFilePath))
                {
                    try { File.Delete(tempZipFilePath); }
                    catch (Exception ex) { OnProgressReported($"[WARN] Failed to delete temp zip file {tempZipFilePath}: {ex.Message}"); }
                }
                if (Directory.Exists(tempExtractDirPath))
                {
                    try { DeleteDirectoryRecursively(tempExtractDirPath); } // Use your recursive delete
                    catch (Exception ex) { OnProgressReported($"[WARN] Failed to delete temp extract dir {tempExtractDirPath}: {ex.Message}"); }
                }
            }
        }

        private async Task MergeDirectoryAsync(string sourceDir, string targetDir, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(targetDir); // Ensure target directory exists

            // Process files
            foreach (string sourceFile in Directory.GetFiles(sourceDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(sourceFile);
                string targetFile = Path.Combine(targetDir, fileName);

                // Your Java version had filesAreIdentical. Let's replicate a simplified version.
                // For a full async version, FilesAreIdenticalAsync would be better.
                bool identical = File.Exists(targetFile) && await FilesAreIdenticalAsync(sourceFile, targetFile, cancellationToken);

                if (!identical)
                {
                    OnProgressReported($"Copying: {fileName} to {Path.GetDirectoryName(targetFile)}");
                    File.Copy(sourceFile, targetFile, true); // Overwrite if exists
                }
                else
                {
                    // OnProgressReported($"Skipped identical file: {fileName}");
                }
            }

            // Process subdirectories
            foreach (string sourceSubDir in Directory.GetDirectories(sourceDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string dirName = Path.GetFileName(sourceSubDir);
                string targetSubDir = Path.Combine(targetDir, dirName);
                await MergeDirectoryAsync(sourceSubDir, targetSubDir, cancellationToken); // Recursive call
            }
        }

        private async Task<bool> FilesAreIdenticalAsync(string filePath1, string filePath2, CancellationToken cancellationToken)
        {
            const int bufferSize = 8192; // 8KB buffer

            FileInfo info1 = new FileInfo(filePath1);
            FileInfo info2 = new FileInfo(filePath2);

            if (info1.Length != info2.Length)
                return false;

            if (info1.Length == 0) // Both are empty
                return true;

            // For .NET Framework 4.7.2, FileStream has an async constructor overload,
            // but ReadAsync is the primary async method.
            using (FileStream fs1 = new FileStream(filePath1, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
            using (FileStream fs2 = new FileStream(filePath2, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
            {
                byte[] buffer1 = new byte[bufferSize];
                byte[] buffer2 = new byte[bufferSize];

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int bytesRead1 = await fs1.ReadAsync(buffer1, 0, bufferSize, cancellationToken);
                    int bytesRead2 = await fs2.ReadAsync(buffer2, 0, bufferSize, cancellationToken);

                    if (bytesRead1 != bytesRead2)
                        return false; // Should not happen if lengths are same

                    if (bytesRead1 == 0) // End of both files
                        return true;

                    for (int i = 0; i < bytesRead1; i++)
                    {
                        if (buffer1[i] != buffer2[i])
                            return false;
                    }
                }
            }
        }

        private void DeleteDirectoryRecursively(string path)
        {
            if (!Directory.Exists(path)) return;

            // Delete all files in the directory
            foreach (string file in Directory.GetFiles(path))
            {
                File.SetAttributes(file, FileAttributes.Normal); // Ensure not read-only
                File.Delete(file);
            }

            // Recursively delete all subdirectories
            foreach (string dir in Directory.GetDirectories(path))
            {
                DeleteDirectoryRecursively(dir);
            }

            // Delete the now-empty directory itself
            Directory.Delete(path, false);
            OnProgressReported($"Cleaned up directory: {path}");
        }


        public void Dispose()
        {
            _httpClient?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}