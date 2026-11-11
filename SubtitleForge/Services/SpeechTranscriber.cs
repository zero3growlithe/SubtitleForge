using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SubtitleForge.Models;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace SubtitleForge.Services
{
    public class TranscriptionRequest
    {
        public string WavPath { get; set; } = string.Empty;

        public string ModelPath { get; set; } = string.Empty;

        public string Language { get; set; } = "auto";

        public bool TranslateToEnglish { get; set; }

        public bool UseGpu { get; set; } = true;

        public bool SkipNonSpeech { get; set; } = true;

        public int Threads { get; set; } = 4;

        public string? InitialPrompt { get; set; }
    }

    public class TranscriptionResult
    {
        public List<SubtitleCue> Cues { get; set; } = new List<SubtitleCue>();

        public string RuntimeDescription { get; set; } = "CPU";

        public string DetectedLanguage { get; set; } = string.Empty;

        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// Runs whisper.cpp through Whisper.net: CUDA when a driver is present, CPU otherwise.
    /// </summary>
    public class SpeechTranscriber
    {
        public async Task<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            IProgress<double>? progress,
            IProgress<string>? status,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(request.WavPath))
            {
                throw new FileNotFoundException("The extracted audio file is missing.", request.WavPath);
            }

            if (!File.Exists(request.ModelPath))
            {
                throw new FileNotFoundException("The Whisper model file is missing.", request.ModelPath);
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            TranscriptionResult result = new TranscriptionResult();
            List<SubtitleCue> cues = new List<SubtitleCue>();
            string detectedLanguage = string.Empty;

            string cudaReason;
            bool gpuRequested = request.UseGpu && WhisperRuntimeProbe.IsCudaAvailable(out cudaReason);
            if (request.UseGpu && !gpuRequested)
            {
                status?.Report("GPU not used (" + cudaReason + ") — running on CPU.");
            }

            WhisperFactory? factory = null;
            if (gpuRequested)
            {
                RuntimeOptions.RuntimeLibraryOrder = WhisperRuntimeProbe.AutoOrder;
                try
                {
                    factory = WhisperFactory.FromPath(request.ModelPath, new WhisperFactoryOptions { UseGpu = true });
                }
                catch (Exception exception)
                {
                    // Missing cuBLAS / cuDNN or an unusable device: fall back instead of failing the job.
                    CrashLog.Write(exception, "SpeechTranscriber.GpuFactory");
                    status?.Report("CUDA could not be initialised — falling back to CPU.");
                    factory = null;
                }
            }

            if (factory == null)
            {
                RuntimeOptions.RuntimeLibraryOrder = WhisperRuntimeProbe.CpuOnlyOrder;
                factory = WhisperFactory.FromPath(request.ModelPath, new WhisperFactoryOptions { UseGpu = false });
            }

            result.RuntimeDescription = WhisperRuntimeProbe.Describe(RuntimeOptions.LoadedLibrary, request.UseGpu);

            using (factory)
            {
                WhisperProcessorBuilder builder = factory.CreateBuilder()
                    .WithThreads(Math.Max(1, request.Threads))
                    .WithLanguage(string.IsNullOrWhiteSpace(request.Language) ? "auto" : request.Language)
                    .SplitOnWord()
                    .WithProgressHandler(delegate (int percent)
                    {
                        if (progress != null)
                        {
                            double fraction = percent / 100.0;
                            if (fraction < 0.0)
                            {
                                fraction = 0.0;
                            }

                            if (fraction > 1.0)
                            {
                                fraction = 1.0;
                            }

                            progress.Report(fraction);
                        }
                    });

                if (request.TranslateToEnglish)
                {
                    builder = builder.WithTranslate();
                }

                if (!string.IsNullOrWhiteSpace(request.InitialPrompt))
                {
                    builder = builder.WithPrompt(request.InitialPrompt!);
                }

                using (WhisperProcessor processor = builder.Build())
                using (FileStream waveStream = File.OpenRead(request.WavPath))
                {
                    await Task.Run(
                        async delegate
                        {
                            await foreach (SegmentData segment in processor
                                .ProcessAsync(waveStream, cancellationToken)
                                .ConfigureAwait(false))
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                string text = (segment.Text ?? string.Empty).Trim();
                                if (text.Length == 0)
                                {
                                    continue;
                                }

                                if (request.SkipNonSpeech && IsNonSpeech(text))
                                {
                                    continue;
                                }

                                TimeSpan start = segment.Start;
                                TimeSpan end = segment.End;
                                if (end <= start)
                                {
                                    double estimated = Math.Max(0.4, text.Length * 0.045);
                                    end = start + TimeSpan.FromSeconds(estimated);
                                }

                                cues.Add(new SubtitleCue(start, end, text, segment.Probability));

                                if (detectedLanguage.Length == 0 && !string.IsNullOrWhiteSpace(segment.Language))
                                {
                                    detectedLanguage = segment.Language;
                                }
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                }
            }

            stopwatch.Stop();
            result.Cues = cues;
            result.DetectedLanguage = detectedLanguage;
            result.Elapsed = stopwatch.Elapsed;
            progress?.Report(1.0);
            return result;
        }

        /// <summary>
        /// Rejects markers such as [Music], (applause) or bare ♪ that Whisper emits for non-speech audio.
        /// </summary>
        public static bool IsNonSpeech(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            string trimmed = text.Trim();

            bool hasLetterOrDigit = false;
            foreach (char character in trimmed)
            {
                if (char.IsLetterOrDigit(character))
                {
                    hasLetterOrDigit = true;
                    break;
                }
            }

            if (!hasLetterOrDigit)
            {
                return true;
            }

            char first = trimmed[0];
            char last = trimmed[trimmed.Length - 1];
            bool wrappedInBrackets = (first == '[' && last == ']') || (first == '(' && last == ')');
            if (wrappedInBrackets)
            {
                return true;
            }

            if (trimmed.StartsWith("♪", StringComparison.Ordinal) && trimmed.EndsWith("♪", StringComparison.Ordinal) && trimmed.Length <= 4)
            {
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Reports which native whisper.cpp build is going to be used.
    /// </summary>
    public static class WhisperRuntimeProbe
    {
        private static readonly List<RuntimeLibrary> CapturedDefaultOrder = CaptureDefaultOrder();

        public static bool IsCudaDriverPresent()
        {
            try
            {
                string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (string.IsNullOrEmpty(systemDirectory))
                {
                    return false;
                }

                return File.Exists(Path.Combine(systemDirectory, "nvcuda.dll"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The library order whisper.net uses when nothing is forced (captured once, before we touch it).
        /// </summary>
        public static List<RuntimeLibrary> AutoOrder
        {
            get { return new List<RuntimeLibrary>(CapturedDefaultOrder); }
        }

        public static List<RuntimeLibrary> CpuOnlyOrder
        {
            get { return new List<RuntimeLibrary> { RuntimeLibrary.Cpu }; }
        }

        /// <summary>
        /// True when the GPU path has a real chance: driver present *and* the CUDA 12
        /// runtime DLLs whisper.cpp links against (cuBLAS / cudart) are reachable.
        /// </summary>
        public static bool IsCudaAvailable(out string reason)
        {
            if (!IsCudaDriverPresent())
            {
                reason = "no NVIDIA driver (nvcuda.dll missing)";
                return false;
            }

            if (!AreCudaLibrariesReachable())
            {
                reason = "CUDA 12 runtime (cublas64_12 / cudart64_12) not found";
                return false;
            }

            reason = "CUDA 12";
            return true;
        }

        public static bool AreCudaLibrariesReachable()
        {
            return IsLibraryReachable("cublas64_12.dll") && IsLibraryReachable("cudart64_12.dll");
        }

        private static bool IsLibraryReachable(string fileName)
        {
            try
            {
                string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (!string.IsNullOrEmpty(systemDirectory) && File.Exists(Path.Combine(systemDirectory, fileName)))
                {
                    return true;
                }

                string? pathVariable = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathVariable))
                {
                    foreach (string folder in pathVariable.Split(Path.PathSeparator))
                    {
                        if (string.IsNullOrWhiteSpace(folder))
                        {
                            continue;
                        }

                        try
                        {
                            if (File.Exists(Path.Combine(folder.Trim(), fileName)))
                            {
                                return true;
                            }
                        }
                        catch (Exception)
                        {
                            // Unusable PATH entry — keep looking.
                        }
                    }
                }

                string toolkitRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "NVIDIA GPU Computing Toolkit",
                    "CUDA");
                if (Directory.Exists(toolkitRoot))
                {
                    foreach (string versionFolder in Directory.GetDirectories(toolkitRoot))
                    {
                        try
                        {
                            if (File.Exists(Path.Combine(versionFolder, "bin", fileName)))
                            {
                                return true;
                            }
                        }
                        catch (Exception)
                        {
                            // Skip inaccessible folders.
                        }
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        private static List<RuntimeLibrary> CaptureDefaultOrder()
        {
            try
            {
                List<RuntimeLibrary> current = RuntimeOptions.RuntimeLibraryOrder;
                if (current != null && current.Count > 0)
                {
                    return new List<RuntimeLibrary>(current);
                }
            }
            catch (Exception)
            {
                // Fall through to the conservative default below.
            }

            return new List<RuntimeLibrary> { RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu };
        }

        public static string Describe(RuntimeLibrary? library, bool gpuRequested)
        {
            if (!library.HasValue)
            {
                return "CPU (unknown library)";
            }

            switch (library.Value)
            {
                case RuntimeLibrary.Cuda:
                    return "GPU — CUDA 13";
                case RuntimeLibrary.Cuda12:
                    return "GPU — CUDA 12";
                case RuntimeLibrary.Vulkan:
                    return "GPU — Vulkan";
                case RuntimeLibrary.OpenVino:
                    return "GPU — OpenVINO";
                case RuntimeLibrary.CoreML:
                    return "GPU — CoreML";
                case RuntimeLibrary.Cpu:
                    return gpuRequested ? "CPU (GPU fallback)" : "CPU";
                case RuntimeLibrary.CpuNoAvx:
                    return "CPU (no AVX)";
                default:
                    return library.Value.ToString();
            }
        }
    }
}
