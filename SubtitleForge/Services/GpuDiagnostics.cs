using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.LibraryLoader;

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

            CudaSearchPath.Apply();

            AppendDriver(report);
            AppendToolkit(report);
            AppendCudaLibraries(report);
            AppendLoaderSearchPaths(report);
            AppendAttempt(report, modelPath, ref gpuUsable);
            AppendDecode(report, modelPath, ref gpuUsable);

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
            report.AppendLine("   loadable = Windows can open it by name right now, which is the only thing that counts");
            report.AppendLine("   cuBLAS and the CUDA runtime are what ggml-cuda needs; cuDNN is listed because the"
                + " toolkit installs it, not because whisper.cpp uses it");

            foreach (string library in CudaLibraries)
            {
                string bareName = Path.GetFileNameWithoutExtension(library);
                string found = Locate(library);
                string loadable;
                string loadError;
                bool canLoad = TryLoadByName(bareName, out loadError);
                if (canLoad)
                {
                    loadable = "yes";
                }
                else
                {
                    loadable = "NO (error " + loadError + ")";
                }

                bool optional = library.StartsWith("cudnn", StringComparison.OrdinalIgnoreCase);
                report.AppendLine("   " + library.PadRight(18) + "loadable: " + loadable.PadRight(16)
                    + (found.Length == 0 ? "not installed" : found)
                    + (optional && !canLoad ? "  (optional)" : string.Empty));
            }

            string? applied = CudaSearchPath.AppliedFolder;
            if (applied == null)
            {
                report.AppendLine("   search path: no CUDA bin folder could be added to the DLL search path");
            }
            else
            {
                report.AppendLine("   search path: added " + applied + " to the DLL search path");
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

        /// <summary>
        /// Mirrors what whisper.net does: <c>NativeLibrary.TryLoad</c> with the bare library name.
        /// </summary>
        private static bool TryLoadByName(string name, out string errorCode)
        {
            errorCode = string.Empty;
            try
            {
                if (NativeLibrary.TryLoad(name, out IntPtr handle))
                {
                    NativeLibrary.Free(handle);
                    return true;
                }

                errorCode = Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                return false;
            }
            catch (Exception exception)
            {
                errorCode = exception.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// Walks the folders whisper.net's own loader probes, since a native DLL in the wrong folder
        /// is invisible to it and that failure is completely silent.
        /// </summary>
        private static void AppendLoaderSearchPaths(StringBuilder report)
        {
            report.AppendLine("4. Where whisper.net looks for the runtime");
            report.AppendLine("   library path override: "
                + (string.IsNullOrEmpty(RuntimeOptions.LibraryPath) ? "(none)" : RuntimeOptions.LibraryPath));
            report.AppendLine("   app folder:            " + AppContext.BaseDirectory);
            report.AppendLine("   current order:         " + DescribeOrder(RuntimeOptions.RuntimeLibraryOrder));

            foreach (string candidate in ProbedRuntimeFolders())
            {
                report.AppendLine("   " + candidate);
            }

            string cudaFolder = Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda12", "win-x64");
            report.AppendLine("   files the CUDA folder must contain:");
            foreach (string required in new string[] { "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cuda-whisper.dll" })
            {
                string path = Path.Combine(cudaFolder, required);
                bool exists = false;
                try
                {
                    exists = File.Exists(path);
                }
                catch (Exception)
                {
                    exists = false;
                }

                report.AppendLine("     " + (exists ? "present: " : "MISSING: ") + required);
            }

            string misplaced = Path.Combine(AppContext.BaseDirectory, "ggml-cuda-whisper.dll");
            bool misplacedExists = false;
            try
            {
                misplacedExists = File.Exists(misplaced);
            }
            catch (Exception)
            {
                misplacedExists = false;
            }

            if (misplacedExists && !Directory.Exists(cudaFolder))
            {
                report.AppendLine("   note: ggml-cuda-whisper.dll sits in the app folder, but the loader never looks there.");
                report.AppendLine("         It belongs in " + cudaFolder + " together with whisper.dll, ggml-whisper.dll");
                report.AppendLine("         and ggml-base-whisper.dll. The published build already lays them out that way.");
            }

            report.AppendLine();
        }

        private static IEnumerable<string> ProbedRuntimeFolders()
        {
            List<string> lines = new List<string>();
            string runtimesRoot = Path.Combine(AppContext.BaseDirectory, "runtimes");

            foreach (RuntimeLibrary library in RuntimeOptions.RuntimeLibraryOrder)
            {
                string relative = library switch
                {
                    RuntimeLibrary.Cuda => Path.Combine("cuda", "win-x64"),
                    RuntimeLibrary.Cuda12 => Path.Combine("cuda12", "win-x64"),
                    RuntimeLibrary.Vulkan => Path.Combine("vulkan", "win-x64"),
                    RuntimeLibrary.Cpu => "win-x64",
                    RuntimeLibrary.CpuNoAvx => Path.Combine("noavx", "win-x64"),
                    RuntimeLibrary.CoreML => Path.Combine("coreml", "win-x64"),
                    RuntimeLibrary.OpenVino => Path.Combine("openvino", "win-x64"),
                    _ => library.ToString()
                };

                string folder = Path.Combine(runtimesRoot, relative);
                bool exists = false;
                try
                {
                    exists = Directory.Exists(folder);
                }
                catch (Exception)
                {
                    exists = false;
                }

                lines.Add((exists ? "checked:   " : "not there: ") + library + " -> " + DescribeFolder(folder));
            }

            return lines;
        }

        private static string DescribeOrder(List<RuntimeLibrary> order)
        {
            if (order == null || order.Count == 0)
            {
                return "(empty)";
            }

            List<string> names = new List<string>();
            foreach (RuntimeLibrary library in order)
            {
                names.Add(library.ToString());
            }

            return string.Join(" -> ", names.ToArray());
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

            if (RuntimeOptions.LoadedLibrary.HasValue)
            {
                report.AppendLine("   note: whisper.net already loaded " + RuntimeOptions.LoadedLibrary.Value
                    + " earlier in this process and never reloads. Restart the app and run the diagnostics");
                report.AppendLine("         first if the result below looks inherited.");
            }

            try
            {
                RuntimeOptions.RuntimeLibraryOrder = WhisperRuntimeProbe.AutoOrder;
                using (WhisperFactory factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true }))
                {
                    // FromPath does not throw when CUDA is unavailable: the loader quietly walks on to
                    // the next runtime in the order. The loaded library is the only honest answer.
                    RuntimeLibrary? loaded = RuntimeOptions.LoadedLibrary;
                    string loadedText = WhisperRuntimeProbe.Describe(loaded, true);
                    report.AppendLine("   loaded library: " + loadedText);

                    if (loaded == RuntimeLibrary.Cuda || loaded == RuntimeLibrary.Cuda12)
                    {
                        // This only says a CUDA build was found and opened. Whether the arithmetic
                        // ends up on the device is a different question, answered in step 6.
                        report.AppendLine("   the CUDA build was loaded: " + loadedText + ".");
                        report.AppendLine("   whether it also RUNS on the device is the next step, because a");
                        report.AppendLine("   CUDA build of whisper.dll will happily load and then use the CPU.");
                    }
                    else
                    {
                        report.AppendLine("   FAILED: whisper.net fell back to " + loadedText + " even though CUDA was requested.");
                        report.AppendLine("   A CUDA attempt is skipped when either check below fails:");
                        report.AppendLine("     a) runtimes\\cuda12\\win-x64 must hold whisper.dll and ggml-cuda-whisper.dll (section 4)");
                        report.AppendLine("     b) cudart64_12.dll must be loadable by name (section 3)");
                    }
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

        /// <summary>
        /// The only check that cannot be fooled: decode a second of audio and read whisper.cpp's own
        /// log for the backend it initialised, then confirm the process holds memory on the device.
        /// A CUDA build can be loaded, opened and then run the CPU backend when its ggml-cuda
        /// companion is missing or will not initialise, and that is exactly the case this catches.
        /// </summary>
        private static void AppendDecode(StringBuilder report, string? modelPath, ref bool gpuUsable)
        {
            report.AppendLine();
            report.AppendLine("6. Decoding one second of audio to see which backend whisper really uses");

            // ggml finds backends by scanning the executable folder and the current directory, so a
            // module that lives in runtimes\cuda12\win-x64 has to be registered through this variable
            // or whisper.cpp never sees it.
            string backendPath = GgmlBackendPath.Apply();
            report.AppendLine("   backend module handed to ggml: " + backendPath);
            report.AppendLine("   " + GgmlBackendPath.VariableName + " = "
                + (GgmlBackendPath.CurrentValue().Length > 0 ? GgmlBackendPath.CurrentValue() : "(not set)"));
            report.AppendLine("   CUDA devices:");
            report.AppendLine(CudaDevices.DescribeSelection(App.Settings.CudaDeviceIndex));

            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                report.AppendLine("   skipped: no model file to open");
                return;
            }

            string wavPath = string.Empty;
            try
            {
                wavPath = WriteSilentWave();

                RuntimeOptions.RuntimeLibraryOrder = WhisperRuntimeProbe.AutoOrder;
                using (WhisperFactory factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true }))
                using (WhisperBackendVerification verification = new WhisperBackendVerification())
                {
                    WhisperProcessor processor = factory.CreateBuilder()
                        .WithThreads(2)
                        .WithNoContext()
                        .WithLanguage("en")
                        .Build();

                    try
                    {
                        using (FileStream waveStream = File.OpenRead(wavPath))
                        {
                            int segments = 0;
                            IAsyncEnumerator<SegmentData> enumerator = processor
                                .ProcessAsync(waveStream, CancellationToken.None)
                                .GetAsyncEnumerator();
                            try
                            {
                                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                                {
                                    segments++;
                                    if (segments > 8)
                                    {
                                        break;
                                    }
                                }
                            }
                            finally
                            {
                                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            processor.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                        catch (Exception)
                        {
                        }
                    }

                    if (verification.Kind == WhisperBackendKind.Cuda)
                    {
                        report.AppendLine("   OK: whisper initialised " + verification.Describe() + ".");
                        if (verification.CudaDeviceCount > 0)
                        {
                            report.AppendLine("   CUDA devices reported: "
                                + verification.CudaDeviceCount.ToString(CultureInfo.InvariantCulture));
                        }

                        string videoMemoryDetail;
                        long videoMemory = NvidiaSmiProbe.GetProcessVideoMemory(Environment.ProcessId, out videoMemoryDetail);
                        if (videoMemory > 0)
                        {
                            report.AppendLine("   this process holds " + DescribeMegabytes(videoMemory)
                                + " of device memory: the tensors really are on the GPU.");
                        }
                        else
                        {
                            report.AppendLine("   note: nvidia-smi does not list this process (" + videoMemoryDetail + ").");
                            report.AppendLine("         On a laptop with switchable graphics that can mean the driver");
                            report.AppendLine("         gave the work to another adapter; check with nvidia-smi dmon.");
                        }

                        gpuUsable = true;
                    }
                    else if (verification.Kind == WhisperBackendKind.Cpu)
                    {
                        report.AppendLine("   FAILED: the CUDA build loaded but whisper initialised the CPU backend.");
                        report.AppendLine("   whisper said: " + verification.Evidence);
                        report.AppendLine("   The usual reasons, cheapest to check first:");
                        report.AppendLine("     a) the CUDA backend module was never registered with ggml: it only scans the");
                        report.AppendLine("        app folder and the current directory, which is why GGML_BACKEND_PATH above");
                        report.AppendLine("        has to point at runtimes\\cuda12\\win-x64\\ggml-cuda-whisper.dll");
                        report.AppendLine("     b) ggml-cuda-whisper.dll is missing from runtimes\\cuda12\\win-x64 (section 4 lists");
                        report.AppendLine("        what is in the folder)");
                        report.AppendLine("     c) the files in that folder come from a different whisper.net version than");
                        report.AppendLine("        Whisper.net.dll, or the CUDA libraries are the wrong major version (CUDA 12)");
                        report.AppendLine("   Whisper's own log lines:");
                        report.AppendLine(verification.DescribeEvidence());
                        gpuUsable = false;
                    }
                    else
                    {
                        report.AppendLine("   inconclusive: whisper logged nothing about its backend.");
                        report.AppendLine("   Log lines seen:");
                        string evidence = verification.DescribeEvidence();
                        report.AppendLine(evidence.Length > 0 ? evidence : "     (none)");
                    }
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

                CrashLog.Write(exception, "GpuDiagnostics.Decode");
            }
            finally
            {
                RuntimeOptions.RuntimeLibraryOrder = WhisperRuntimeProbe.CpuOnlyOrder;
                TryDelete(wavPath);
            }
        }

        /// <summary>One second of silence as 16 kHz mono 16-bit PCM, enough for whisper to initialise.</summary>
        private static string WriteSilentWave()
        {
            string path = Path.Combine(Path.GetTempPath(), "subtitleforge-gpudiag-" + Guid.NewGuid().ToString("N") + ".wav");
            const int sampleRate = 16000;
            const int sampleCount = sampleRate;

            using (FileStream stream = File.Create(path))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int dataBytes = sampleCount * 2;
                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(dataBytes);
                writer.Write(new byte[dataBytes]);
            }

            return path;
        }

        private static void TryDelete(string path)
        {
            if (path.Length == 0)
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        private static string DescribeMegabytes(long bytes)
        {
            const double Megabyte = 1024.0 * 1024.0;
            double megabytes = bytes / Megabyte;
            if (megabytes >= 1024.0)
            {
                return (megabytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
            }

            return megabytes.ToString("0", CultureInfo.InvariantCulture) + " MB";
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
