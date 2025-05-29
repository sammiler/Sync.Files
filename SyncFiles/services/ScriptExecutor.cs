using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text; // For StringBuilder if collecting all output at once
using System.Threading.Tasks;

namespace SyncFiles.Core.Services
{
    public class ScriptExecutionResult
    {
        public int ExitCode { get; }
        public string StandardOutput { get; } // Aggregated standard output
        public string StandardError { get; }  // Aggregated standard error
        public bool Success => ExitCode == 0;

        public ScriptExecutionResult(int exitCode, string standardOutput, string standardError)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput ?? string.Empty;
            StandardError = standardError ?? string.Empty;
        }
    }
    public enum ExternalTerminalType
    {
        Cmd,
        PowerShell
    }

    public class ScriptExecutor
    {
        private readonly string _defaultWorkingDirectory;

        public ScriptExecutor(string defaultWorkingDirectory = null)
        {
            if (!string.IsNullOrEmpty(defaultWorkingDirectory) && !Directory.Exists(defaultWorkingDirectory))
            {
                Console.WriteLine($"[WARN] Default working directory for ScriptExecutor does not exist: {defaultWorkingDirectory}");
                _defaultWorkingDirectory = null;
            }
            else
            {
                _defaultWorkingDirectory = defaultWorkingDirectory;
            }
        }

        // ... (已有的 ExecuteAndCaptureOutputAsync 方法) ...
        public Task<ScriptExecutionResult> ExecuteAndCaptureOutputAsync(
            string pythonExecutable,
            string scriptPath,
            IEnumerable<string> arguments,
            Dictionary<string, string> environmentVariables,
            string workingDirectory,
            Action<string> onOutputDataReceived,
            Action<string> onErrorDataReceived)
        {
            // ... (之前的实现保持不变) ...
            if (string.IsNullOrWhiteSpace(pythonExecutable))
                throw new ArgumentNullException(nameof(pythonExecutable), "Python executable path cannot be null or empty.");
            if (string.IsNullOrWhiteSpace(scriptPath))
                throw new ArgumentNullException(nameof(scriptPath), "Script path cannot be null or empty.");
            if (!File.Exists(pythonExecutable))
                throw new FileNotFoundException($"Python executable not found at: {pythonExecutable}", pythonExecutable);
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Python script not found at: {scriptPath}", scriptPath);

            var tcs = new TaskCompletionSource<ScriptExecutionResult>();

            var process = new Process();
            try
            {
                process.StartInfo.FileName = pythonExecutable;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.RedirectStandardInput = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                var argsBuilder = new StringBuilder();
                argsBuilder.Append($"\"{scriptPath}\"");
                if (arguments != null)
                {
                    foreach (string arg in arguments)
                    {
                        argsBuilder.Append($" \"{arg.Replace("\"", "\\\"")}\"");
                    }
                }
                process.StartInfo.Arguments = argsBuilder.ToString();

                if (!string.IsNullOrWhiteSpace(workingDirectory))
                {
                    if (Directory.Exists(workingDirectory))
                    {
                        process.StartInfo.WorkingDirectory = workingDirectory;
                    }
                    else
                    {
                        Console.WriteLine($"[WARN] Specified working directory '{workingDirectory}' does not exist. Falling back.");
                        process.StartInfo.WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? _defaultWorkingDirectory ?? Environment.CurrentDirectory;
                    }
                }
                else
                {
                    process.StartInfo.WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? _defaultWorkingDirectory ?? Environment.CurrentDirectory;
                }
                Console.WriteLine($"[INFO] Script working directory set to: {process.StartInfo.WorkingDirectory}");

                if (environmentVariables != null)
                {
                    foreach (var kvp in environmentVariables)
                    {
                        process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
                    }
                }
                process.StartInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
                process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "UTF-8";

                var stdOutBuffer = new StringBuilder();
                var stdErrBuffer = new StringBuilder();

                process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        stdOutBuffer.AppendLine(args.Data);
                        onOutputDataReceived?.Invoke(args.Data);
                    }
                };

                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        stdErrBuffer.AppendLine(args.Data);
                        onErrorDataReceived?.Invoke(args.Data);
                    }
                };

                process.EnableRaisingEvents = true;
                process.Exited += (sender, args) =>
                {
                    try
                    {
                        int exitCode = process.ExitCode;
                        tcs.TrySetResult(new ScriptExecutionResult(exitCode, stdOutBuffer.ToString(), stdErrBuffer.ToString()));
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine($"[ERROR] InvalidOperationException on process exit: {ex.Message}. Assuming error exit.");
                        tcs.TrySetResult(new ScriptExecutionResult(-1, stdOutBuffer.ToString(), stdErrBuffer.ToString() + Environment.NewLine + "Process exit state error: " + ex.Message));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Exception on process exit: {ex.Message}.");
                        tcs.TrySetResult(new ScriptExecutionResult(-1, stdOutBuffer.ToString(), stdErrBuffer.ToString() + Environment.NewLine + "Generic process exit error: " + ex.Message));
                    }
                    finally
                    {
                        process.Dispose();
                    }
                };

                Console.WriteLine($"[INFO] Executing (capture mode): {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                if (!process.Start())
                {
                    tcs.TrySetResult(new ScriptExecutionResult(-1, "", "Failed to start process."));
                    process.Dispose();
                    return tcs.Task;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Exception setting up or starting process for script '{scriptPath}': {ex.Message}");
                tcs.TrySetResult(new ScriptExecutionResult(-1, "", $"Setup/Start Error: {ex.Message}"));
                if (process != null && !process.HasExited)
                {
                    try { process.Kill(); } catch { /* ignored */ }
                }
                process?.Dispose();
            }
            return tcs.Task;
        }


        /// <summary>
        /// Launches a Python script in a new external terminal window (CMD or PowerShell).
        /// The terminal window will remain open after the script finishes.
        /// </summary>
        /// <param name="pythonExecutable">Full path to the Python interpreter.</param>
        /// <param name="scriptPath">Full path to the Python script to execute.</param>
        /// <param name="scriptArgumentsAsString">A single string containing all arguments for the script (e.g., "arg1 \"arg two\"").</param>
        /// <param name="windowTitle">Custom title for the new terminal window. If null or empty, a default title based on script name might be used.</param>
        /// <param name="environmentVariables">Custom environment variables for the script's process.</param>
        /// <param name="workingDirectory">The working directory for the script execution.</param>
        /// <param name="terminalType">Specifies whether to use CMD or PowerShell.</param>
        public void LaunchInExternalTerminal(
            string pythonExecutable,
            string scriptPath,
            string scriptArgumentsAsString, // Changed to string for easier command line construction
            string windowTitle,
            Dictionary<string, string> environmentVariables,
            string workingDirectory,
            ExternalTerminalType terminalType = ExternalTerminalType.PowerShell)
        {
            if (string.IsNullOrWhiteSpace(pythonExecutable))
                throw new ArgumentNullException(nameof(pythonExecutable));
            if (string.IsNullOrWhiteSpace(scriptPath))
                throw new ArgumentNullException(nameof(scriptPath));
            if (!File.Exists(pythonExecutable))
                throw new FileNotFoundException($"Python executable not found: {pythonExecutable}", pythonExecutable);
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Python script not found: {scriptPath}", scriptPath);

            // Determine working directory
            string effectiveWorkingDirectory;
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                effectiveWorkingDirectory = workingDirectory;
            }
            else
            {
                effectiveWorkingDirectory = Path.GetDirectoryName(scriptPath) ?? _defaultWorkingDirectory ?? Environment.CurrentDirectory;
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                WorkingDirectory = effectiveWorkingDirectory,
                UseShellExecute = false // Important for setting environment variables for cmd/powershell itself
            };

            // Set environment variables for the new terminal process
            if (environmentVariables != null)
            {
                foreach (var kvp in environmentVariables)
                {
                    startInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
                }
            }
            startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1"; // Good practice

            string finalWindowTitle = string.IsNullOrWhiteSpace(windowTitle) ? Path.GetFileName(scriptPath) : windowTitle;

            if (terminalType == ExternalTerminalType.Cmd)
            {
                startInfo.FileName = "cmd.exe";

                // cmd.exe /K "title "Window Title" && "C:\Python\python.exe" "C:\script.py" arg1 arg2"
                string cmdCommand = $"title \"{finalWindowTitle.Replace("\"", "\"\"")}\" && \"{pythonExecutable}\" \"{scriptPath}\" {scriptArgumentsAsString ?? string.Empty}";
                startInfo.Arguments = $"/S /K \"{cmdCommand.Replace("\"", "\\\"")}\""; // /S strips quotes around /K if cmdCommand has them. Added more robust quoting.
                                                                                       // More robust approach: /K followed by the command string directly.
                                                                                       // Simpler /K structure:
                startInfo.Arguments = $"/K title \"{finalWindowTitle.Replace("\"", "\"\"")}\" && \"{pythonExecutable}\" \"{scriptPath}\" {scriptArgumentsAsString ?? string.Empty}";
            }
            else // PowerShell
            {
                startInfo.FileName = "powershell.exe";

                // PowerShell: $Host.UI.RawUI.WindowTitle = 'Window Title'; & 'C:\Python\python.exe' 'C:\script.py' arg1 arg2
                string psScriptBlock = $"$Host.UI.RawUI.WindowTitle = '{finalWindowTitle.Replace("'", "''")}'; & '{pythonExecutable}' '{scriptPath}' {scriptArgumentsAsString ?? string.Empty}";

                // Using -Command with a script block. If psScriptBlock contains " then they need to be escaped for the outer " of -Command.
                // However, PowerShell is often better with Base64 encoded commands for complexity.
                // For simplicity here, assuming psScriptBlock doesn't have too many problematic chars for simple quoting.
                // Or more safely, pass the command as a single argument to -Command if no complex quoting inside.
                startInfo.Arguments = $"-NoExit -ExecutionPolicy Bypass -Command \"{psScriptBlock.Replace("\"", "`\"")}\""; // `"` escapes " in PS strings
            }

            try
            {
                Console.WriteLine($"[INFO] Launching in external terminal ({terminalType}): {startInfo.FileName} {startInfo.Arguments}");
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to launch script '{scriptPath}' in external {terminalType}: {ex.Message}");
                // Consider throwing a custom exception or notifying UI
                throw new InvalidOperationException($"Failed to launch script in external terminal: {ex.Message}", ex);
            }
        }


    }
}