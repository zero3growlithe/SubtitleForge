using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Whisper.net;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Answers "why is GPU transcription not being used?" with the state of every piece the CUDA
    /// path needs, including a real attempt at opening the selected model with CUDA. Nothing here is
    /// a guess: every line names the file that was looked for and where.
    /// </summary>
    public static class GpuDiagnostics
    {
        private const string ReportFileName = "gpu-diagnostics.txt";

        private static readonly string[] CudaLibraries = new string[]
        {
            "cublas64_12.dll",
            "cublasLt64_12.dll",
            "cudart64_12.dll",
            "cudnn64_9.dll"
        };

        public static string ReportPath
        {
            get
            {
                return Path.Combine(DiagnosticFolder(), ReportFileName);
            }
        }

        /// <summary>
        /// Collects the report. <paramref name="gpuUsable"/> is true only when whisper actually
        /// opened the model through CUDA, which is the one check that cannot lie.
        /// </summary>
        public static string Build(string? modelPath, out bool gpuUsable)
        {
            gpuUsable = false;

            StringBuilder report = new StringBuilder();
            report.AppendLine("SubtitleForge GPU diagnostics");
            report.AppendLine("build: " + BuildInfo.Sha);
            report.AppendLine("system: " + Environment.OSVersion.VersionString
                + " (" + (Environment.Is64BitProcess ? "64-bit" : "32-bit") + " process)");
            report.AppendLine("app folder: " + AppContext.BaseDirectory);
            report.AppendLine();

            AppendDriver(report);
            AppendToolkit(report);
            AppendCudaLibraries(report);
            AppendWhisperRuntime(report);
            AppendAttempt(report, modelPath, ref gpuUsable);

            return report.ToString();
        }

        public static void Save(string report)
        {
            try
            {
                File.WriteAllText(ReportPath, report, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "GpuDiagnostics.Save");
            }
        }

        private static void AppendDriver(StringBuilder report)
        {
            report.AppendLine("1. NVIDIA driver (nvcuda.dll)");
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string driverPath = string.IsNullOrEmpty(systemDirectory)
                ? string.Empty
                : Path.Combine(systemDirectory, "nvcuda.dll");

            if (driverPath.Length > 0 && File.Exists(driverPath))
            {
                report.AppendLine("   found:   " + driverPath);
                report.AppendLine("   version: " + FileVersion(driverPath));
            }
            else
            {
                report.AppendLine("   MISSING: " + (driverPath.Length == 0 ? "(no system folder)" : driverPath));
            }

            report.AppendLine();
        }

        private static void AppendToolkit(StringBuilder report)
        {
            report.AppendLine("2. CUDA 12 toolkit (nvcc.exe)");
            string details;
            bool installed = CudaDetector.IsToolkitInstalled(out details);
            report.AppendLine("   " + (installed ? "found:   " : "MISSING: ") + details);

            AppendEnvironmentValue(report, "CUDA_PATH");
            AppendEnvironmentValue(report, "CUDA_HOME");

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA GPU Computing Toolkit",
                "CUDA");
            if (Directory.Exists(root))
            {
                try
                {
                    foreach (string folder in Directory.GetDirectories(root))
                    {
                        report.AppendLine("   installed version: " + Path.GetFileName(folder));
                    }
                }
                catch (Exception)
                {
                    report.AppendLine("   (the toolkit folder could not be listed)");
                }
            }
            else
            {
                report.AppendLine("   no toolkit folder at " + root);
            }

            report.AppendLine();
        }

        private static void AppendCudaLibraries(StringBuilder report)
        {
            report.AppendLine("3. CUDA libraries whisper.cpp links against");
            foreach (string library in CudaLibraries)
            {
                string found = Locate(library);
                report.AppendLine("   " + library.PadRight(18) + (found.Length == 0 ? "not found anywhere" : found));
            }

            report.AppendLine("   PATH entries mentioning CUDA or NVIDIA:");
            bool any = false;
            foreach (string folder in PathFolders())
            {
                if (folder.IndexOf("cuda", StringComparison.OrdinalIgnoreCase) < 0
                    && folder.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                any = true;
                report.AppendLine("     " + folder);
            }

            if (!any)
            {
                report.AppendLine("     (none — the CUDA bin folder is not on PATH)");
            }

            report.AppendLine();
        }

        private static void AppendWhisperRuntime(StringBuilder report)
        {
            report.AppendLine("4. CUDA whisper natives next to the app");
            string details;
            bool present = CudaDetector.IsRuntimePresent(out details);
            report.AppendLine("   " + (present ? "found:   " : "MISSING: ") + details);

            string[] folders = new string[]
            {
                Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda12", "win-x64"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64"),
                AppContext.BaseDirectory
            };

            foreach (string folder in folders)
            {
                report.AppendLine("   " + folder + ": " + DescribeFolder(folder));
            }

            report.AppendLine();
        }

        private static void AppendAttempt(StringBuilder report, string? modelPath, ref bool gpuUsable)
        {
            report.AppendLine("5. Opening the model through CUDA");

            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                report.AppendLine("   skipped: no model file to open (" + (modelPath ?? "none") + ")");
                report.AppendLine("   Pick a model and transcribe once, then run the diagnostics again.");
                return;
            }

            report.AppendLine("   model: " + modelPath);

            string reason;
            bool preCheck = WhisperRuntimeProbe.IsCudaAvailable(out reason);
            report.AppendLine("   pre-check: " + (preCheck ? "pass" : "fail") + " (" + reason + ")");

            try
            {
                RuntimeOptions.RuntimeLibraryOrder = WhisperRuntimeProbe.AutoOrder;
                using (WhisperFactory factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true }))
                {
                    report.AppendLine("   OK: the model opened through CUDA.");
                    report.AppendLine("   loaded library: " + WhisperRuntimeProbe.Describe(RuntimeOptions.LoadedLibrary, true));
                    gpuUsable = true;
                }
            }
            catch (Exception exception)
            {
                report.AppendLine("   FAILED: " + exception.GetType().Name + ": " + exception.Message);
                Exception? inner = exception.InnerException;
                while (inner != null)
                {
                    report.AppendLine("     caused by: " + inner.GetType().Name + ": " + inner.Message);
                    inner = inner.InnerException;
                }

                report.AppendLine("   The full exception was appended to crash.log.");
                CrashLog.Write(exception, "GpuDiagnostics.Attempt");
            }
            finally
            {
                RuntimeOptions.RuntimeLibraryOrder = WhisperRuntimeProbe.CpuOnlyOrder;
            }
        }

        private static string DescribeFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return "does not exist";
                }

                List<string> names = new List<string>();
                foreach (string file in Directory.GetFiles(folder, "*.dll"))
                {
                    string name = Path.GetFileName(file);
                    if (name.IndexOf("cuda", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("ggml", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("whisper", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        names.Add(name);
                    }
                }

                return names.Count == 0 ? "no whisper or CUDA dlls" : string.Join(", ", names.ToArray());
            }
            catch (Exception)
            {
                return "could not be read";
            }
        }

        /// <summary>
        /// Looks for a DLL the way Windows would: system folder, PATH, then every installed CUDA
        /// toolkit's bin folder.
        /// </summary>
        private static string Locate(string fileName)
        {
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrEmpty(systemDirectory))
            {
                string systemPath = Path.Combine(systemDirectory, fileName);
                if (File.Exists(systemPath))
                {
                    return systemPath;
                }
            }

            foreach (string folder in PathFolders())
            {
                try
                {
                    string candidate = Path.Combine(folder, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // Unusable PATH entry.
                }
            }

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA GPU Computing Toolkit",
                "CUDA");
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (string version in Directory.GetDirectories(root))
                    {
                        string binPath = Path.Combine(version, "bin", fileName);
                        if (File.Exists(binPath))
                        {
                            return binPath;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Skip inaccessible folders.
            }

            string localBin = Path.Combine(AppContext.BaseDirectory, fileName);
            return File.Exists(localBin) ? localBin : string.Empty;
        }

        private static IEnumerable<string> PathFolders()
        {
            List<string> folders = new List<string>();
            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVariable))
            {
                return folders;
            }

            foreach (string folder in pathVariable.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    folders.Add(folder.Trim());
                }
            }

            return folders;
        }

        private static void AppendEnvironmentValue(StringBuilder report, string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            report.AppendLine("   " + name + ": " + (string.IsNullOrEmpty(value) ? "(not set)" : value));
        }

        private static string FileVersion(string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrEmpty(info.FileVersion))
                {
                    return info.FileVersion;
                }
            }
            catch (Exception)
            {
                // Version metadata is a bonus, not the point.
            }

            return "unknown";
        }

        private static string DiagnosticFolder()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SubtitleForge");
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception)
            {
                return Path.GetTempPath();
            }

            return folder;
        }
    }
}
