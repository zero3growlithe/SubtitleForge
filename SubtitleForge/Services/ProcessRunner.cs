using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SubtitleForge.Services
{
    public class ProcessResult
    {
        public int ExitCode { get; set; }

        public string StandardOutput { get; set; } = string.Empty;

        public string StandardError { get; set; } = string.Empty;

        public bool WasCancelled { get; set; }
    }

    /// <summary>
    /// Runs an external tool (ffmpeg / ffprobe) with argument-list quoting, live stdout
    /// callbacks and cancellation that kills the whole process tree.
    /// </summary>
    public static class ProcessRunner
    {
        public static async Task<ProcessResult> RunAsync(
            string executablePath,
            IEnumerable<string> arguments,
            string? workingDirectory,
            Action<string>? onStandardOutputLine,
            CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = executablePath;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = true;

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            ProcessResult result = new ProcessResult();
            StringBuilder standardError = new StringBuilder();
            StringBuilder standardOutput = new StringBuilder();

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs eventArgs)
                {
                    if (eventArgs.Data != null)
                    {
                        standardError.AppendLine(eventArgs.Data);
                    }
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException("Could not start " + executablePath);
                }

                process.BeginErrorReadLine();

                // ffmpeg reads stdin unless -nostdin is passed; close it so it never blocks.
                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception)
                {
                    // Some tools exit before the handle is usable.
                }

                try
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        standardOutput.AppendLine(line);
                        if (onStandardOutputLine != null)
                        {
                            onStandardOutputLine(line);
                        }
                    }

                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    result.ExitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    result.WasCancelled = true;
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                        }
                    }
                    catch (Exception)
                    {
                        // Already gone.
                    }

                    throw;
                }
                finally
                {
                    result.StandardOutput = standardOutput.ToString();
                    result.StandardError = standardError.ToString();
                }
            }

            return result;
        }
    }
}
