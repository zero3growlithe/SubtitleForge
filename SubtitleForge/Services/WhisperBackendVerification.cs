using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Whisper.net.Logger;

namespace SubtitleForge.Services
{
    public enum WhisperBackendKind
    {
        Unknown,
        Cpu,
        Cuda
    }

    /// <summary>
    /// Catches whisper.cpp's own log output while a model is being decoded.
    ///
    /// The library loader reporting "CUDA 12" only means a CUDA build of whisper.dll was picked up
    /// and loaded — it says nothing about the arithmetic. When that build's ggml-cuda companion is
    /// missing, mismatched or cannot initialise, whisper.dll still loads happily from the CUDA folder
    /// and then quietly runs the CPU backend, and the app would go on claiming the GPU. The lines
    /// whisper.cpp prints while it initialises the decoder are the only honest answer: it says
    /// "using CUDA0 backend" when the tensors go to the device and "no GPU found" when they do not.
    /// </summary>
    public sealed class WhisperBackendVerification : IDisposable
    {
        private const int MaximumLines = 500;

        private readonly IDisposable subscription;
        private readonly List<string> lines = new List<string>();
        private readonly object gate = new object();
        private bool disposed;

        public WhisperBackendVerification()
        {
            subscription = LogProvider.AddLogger(Collect);
        }

        public WhisperBackendKind Kind { get; private set; } = WhisperBackendKind.Unknown;

        /// <summary>The adapter whisper picked, for example "NVIDIA GeForce RTX 3060".</summary>
        public string DeviceName { get; private set; } = string.Empty;

        /// <summary>How many CUDA devices the CUDA backend reported at start-up.</summary>
        public int CudaDeviceCount { get; private set; }

        /// <summary>The log line the verdict was taken from.</summary>
        public string Evidence { get; private set; } = string.Empty;

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (gate)
                {
                    return lines.ToArray();
                }
            }
        }

        /// <summary>
        /// "CUDA0 · NVIDIA GeForce RTX 3060" or "CPU (whisper reported no GPU)"; empty while nothing
        /// has been decoded yet.
        /// </summary>
        public string Describe()
        {
            if (Kind == WhisperBackendKind.Cuda)
            {
                if (DeviceName.Length > 0)
                {
                    return "CUDA0 · " + DeviceName;
                }

                return "CUDA0";
            }

            if (Kind == WhisperBackendKind.Cpu)
            {
                return "CPU — whisper initialised the CPU backend";
            }

            return string.Empty;
        }

        /// <summary>Everything that was logged, for when the filtered lines say nothing useful.</summary>
        public string DescribeRaw(int maximum)
        {
            StringBuilder text = new StringBuilder();
            lock (gate)
            {
                int limit = Math.Min(maximum, lines.Count);
                for (int index = 0; index < limit; index++)
                {
                    text.AppendLine("     " + lines[index]);
                }

                if (lines.Count > limit)
                {
                    text.AppendLine("     … " + (lines.Count - limit).ToString(CultureInfo.InvariantCulture)
                        + " more line(s)");
                }
            }

            return text.ToString().TrimEnd();
        }

        /// <summary>The interesting lines only, for a diagnostics report.</summary>
        public string DescribeEvidence()
        {
            StringBuilder text = new StringBuilder();
            lock (gate)
            {
                for (int index = 0; index < lines.Count; index++)
                {
                    string line = lines[index];
                    if (IsInteresting(line))
                    {
                        text.AppendLine("     " + line);
                    }
                }
            }

            return text.ToString().TrimEnd();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                subscription.Dispose();
            }
            catch (Exception)
            {
                // Unsubscribing from a native log cannot be allowed to fail a transcription.
            }
        }

        private static bool IsInteresting(string line)
        {
            return line.IndexOf("cuda", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("backend", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("gpu", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("device", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Collect(WhisperLogLevel level, string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string line = message!.Trim();
            lock (gate)
            {
                if (lines.Count < MaximumLines)
                {
                    lines.Add(line);
                }

                Classify(line);
            }
        }

        /// <summary>
        /// Reads the backend whisper.cpp announced. The verdict is deliberately "last line wins":
        /// a run that first reports no GPU and then initialises CUDA did end up on the device, and
        /// the other way round for a CUDA build that falls back mid-run.
        /// </summary>
        private void Classify(string line)
        {
            if (line.IndexOf("no GPU found", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("whisper_backend_init_cpu", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("using CPU backend", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("ggml_backend_cpu_init", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (Kind != WhisperBackendKind.Cuda)
                {
                    Kind = WhisperBackendKind.Cpu;
                    Evidence = line;
                }

                return;
            }

            if (line.IndexOf("using CUDA", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("whisper_backend_init_gpu: using", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("ggml_backend_cuda_init", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Kind = WhisperBackendKind.Cuda;
                Evidence = line;
                ExtractDeviceName(line);
                return;
            }

            if (line.IndexOf("ggml_cuda_init: found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                CudaDeviceCount = ParseDeviceCount(line);
                return;
            }

            ExtractDeviceName(line);
        }

        private void ExtractDeviceName(string line)
        {
            if (DeviceName.Length > 0)
            {
                return;
            }

            // "  Device 0: NVIDIA GeForce RTX 3060, compute capability 8.6, VMM: yes"
            int marker = line.IndexOf("Device 0:", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                return;
            }

            string rest = line.Substring(marker + "Device 0:".Length).Trim();
            int comma = rest.IndexOf(',');
            if (comma > 0)
            {
                rest = rest.Substring(0, comma).Trim();
            }

            if (rest.Length > 0)
            {
                DeviceName = rest;
            }
        }

        private static int ParseDeviceCount(string line)
        {
            // "ggml_cuda_init: found 1 CUDA devices"
            string[] pieces = line.Split(' ');
            for (int index = 0; index < pieces.Length; index++)
            {
                int number;
                if (int.TryParse(pieces[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                {
                    return number;
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// Asks the NVIDIA driver which processes hold memory on the device. This is the second,
    /// independent witness: a process that is really decoding on the GPU holds VRAM and shows up
    /// here, and nothing about a CUDA library being present makes it appear.
    /// </summary>
    public static class NvidiaSmiProbe
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

        /// <summary>Bytes of VRAM the given process holds, or 0 when it is not on the device.</summary>
        public static long GetProcessVideoMemory(int processId, out string detail)
        {
            detail = string.Empty;
            string output;
            if (!Run("--query-compute-apps=pid,used_gpu_memory --format=csv,noheader,nounits", out output, out detail))
            {
                return 0;
            }

            long total = 0;
            string[] rows = output.Split('\n');
            for (int index = 0; index < rows.Length; index++)
            {
                string row = rows[index].Trim();
                if (row.Length == 0)
                {
                    continue;
                }

                string[] columns = row.Split(',');
                if (columns.Length < 2)
                {
                    continue;
                }

                int pid;
                if (!int.TryParse(columns[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                {
                    continue;
                }

                if (pid != processId)
                {
                    continue;
                }

                long megabytes;
                if (long.TryParse(columns[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out megabytes))
                {
                    total += megabytes * 1024L * 1024L;
                }
            }

            if (total == 0 && detail.Length == 0)
            {
                detail = "this process holds no device memory";
            }

            return total;
        }

        /// <summary>One short line about the first GPU in the machine, for the diagnostics report.</summary>
        public static string DescribeAdapters()
        {
            string output;
            string detail;
            if (!Run("--query-gpu=name,driver_version,memory.total --format=csv,noheader", out output, out detail))
            {
                return "could not ask nvidia-smi: " + detail;
            }

            string text = output.Trim();
            if (text.Length == 0)
            {
                return "nvidia-smi reported no adapters";
            }

            return text.Replace("\n", "; ");
        }

        public static bool IsAvailable()
        {
            string output;
            string detail;
            return Run("--query-gpu=name --format=csv,noheader", out output, out detail);
        }

        private static bool Run(string arguments, out string output, out string detail)
        {
            output = string.Empty;
            detail = string.Empty;
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = "nvidia-smi";
                startInfo.Arguments = arguments;
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.CreateNoWindow = true;

                using (Process process = new Process())
                {
                    process.StartInfo = startInfo;
                    if (!process.Start())
                    {
                        detail = "nvidia-smi did not start";
                        return false;
                    }

                    string standardOutput = process.StandardOutput.ReadToEnd();
                    string standardError = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception)
                        {
                        }

                        detail = "nvidia-smi did not answer in time";
                        return false;
                    }

                    if (process.ExitCode != 0)
                    {
                        detail = "nvidia-smi exited with " + process.ExitCode.ToString(CultureInfo.InvariantCulture)
                            + (standardError.Length > 0 ? ": " + standardError.Trim() : string.Empty);
                        return false;
                    }

                    output = standardOutput;
                    return true;
                }
            }
            catch (Exception exception)
            {
                detail = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }
    }
}
