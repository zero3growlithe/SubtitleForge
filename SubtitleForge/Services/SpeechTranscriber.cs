using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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

        /// <summary>
        /// How many windows may be decoded at the same time. Each one owns a whisper decoder, so
        /// the thread budget is split between them; 1 keeps the old one-at-a-time behaviour.
        /// </summary>
        public int ParallelWindows { get; set; } = 1;

        public string? InitialPrompt { get; set; }
    }

    /// <summary>
    /// One piece of audio to transcribe plus where it belongs on the timeline. Long files are cut
    /// into windows because whisper's alignment drifts and it can start looping when it is handed
    /// many minutes of audio in one go; each window is transcribed independently, which bounds any
    /// drift to that window.
    /// </summary>
    public class TranscriptionWindow
    {
        public string WavPath { get; set; } = string.Empty;

        /// <summary>Seconds added to every cue this window produces.</summary>
        public double OffsetSeconds { get; set; }

        /// <summary>
        /// Cues that start earlier than this many seconds inside the window are dropped, so the
        /// overlap between neighbouring windows is not transcribed twice.
        /// </summary>
        public double SkipBeforeSeconds { get; set; }
    }

    public class TranscriptionResult
    {
        public List<SubtitleCue> Cues { get; set; } = new List<SubtitleCue>();

        public string RuntimeDescription { get; set; } = "CPU";

        /// <summary>
        /// What whisper.cpp itself said it ran on, for example "CUDA0 · NVIDIA GeForce RTX 3060".
        /// Empty when the log said nothing either way.
        /// </summary>
        public string VerifiedBackend { get; set; } = string.Empty;

        public bool VerifiedOnGpu { get; set; }

        /// <summary>VRAM this process holds on the device, 0 when it is not on the device.</summary>
        public long VideoMemoryBytes { get; set; }

        public string DetectedLanguage { get; set; } = string.Empty;

        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// Runs whisper.cpp through Whisper.net: CUDA when a driver is present, CPU otherwise.
    /// </summary>
    public class SpeechTranscriber
    {
        private static readonly TimeSpan DuplicateGapTolerance = TimeSpan.FromSeconds(0.10);
        private static readonly TimeSpan DuplicateDurationTolerance = TimeSpan.FromSeconds(0.10);

        /// <summary>
        /// Transcribes one file. Kept for callers that have a single piece of audio.
        /// </summary>
        public Task<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            IProgress<double>? progress,
            IProgress<string>? status,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(request.WavPath))
            {
                throw new FileNotFoundException("The extracted audio file is missing.", request.WavPath);
            }

            List<TranscriptionWindow> windows = new List<TranscriptionWindow>();
            TranscriptionWindow window = new TranscriptionWindow();
            window.WavPath = request.WavPath;
            windows.Add(window);

            return TranscribeWindowsAsync(request, windows, progress, status, cancellationToken);
        }

        /// <summary>
        /// Transcribes every window with a single loaded model, offsetting each window's cues and
        /// trimming the overlap.
        /// </summary>
        public async Task<TranscriptionResult> TranscribeWindowsAsync(
            TranscriptionRequest request,
            IReadOnlyList<TranscriptionWindow> windows,
            IProgress<double>? progress,
            IProgress<string>? status,
            CancellationToken cancellationToken)
        {
            if (windows.Count == 0)
            {
                throw new FileNotFoundException("No audio windows were supplied.", request.WavPath);
            }

            if (!File.Exists(request.ModelPath))
            {
                throw new FileNotFoundException("The Whisper model file is missing.", request.ModelPath);
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            TranscriptionResult result = new TranscriptionResult();
            List<SubtitleCue> cues = new List<SubtitleCue>();
            string detectedLanguage = string.Empty;
            bool usedWordTimings = false;

            string cudaReason = string.Empty;
            bool gpuRequested = request.UseGpu && WhisperRuntimeProbe.IsCudaAvailable(out cudaReason);
            if (request.UseGpu && !gpuRequested)
            {
                CrashLog.WriteNote("GPU requested but not used: " + cudaReason);
                status?.Report("GPU not used (" + cudaReason + ") — running on CPU.");
            }

            if (gpuRequested)
            {
                // Idempotent: the app shell already did this, but a run must not depend on that.
                GgmlBackendPath.Apply();

                // A CUDA set that is really a copy of the CPU natives loads, opens the model and then
                // runs on the CPU without a single complaint. Say so before the slow part starts.
                string? mixedSet = CudaNativeSet.FindProblem(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda12", "win-x64"),
                    System.IO.Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64"));

                if (mixedSet != null)
                {
                    CrashLog.WriteNote("GPU requested but the CUDA natives are the CPU ones: " + mixedSet);
                    status?.Report("GPU not used (" + mixedSet + ") — running on CPU.");
                    gpuRequested = false;
                }
            }

            // The loader's answer is a claim: a CUDA build of whisper.dll can be loaded and then run
            // the CPU backend when its ggml-cuda companion is missing or will not initialise. Whisper's
            // own native log is the only witness to what the decoder really used, so it is caught for
            // the whole run and read once the run is over.
            using (WhisperBackendVerification verification = new WhisperBackendVerification())
            {
                WhisperFactory? factory = null;
                if (gpuRequested)
                {
                    RuntimeOptions.RuntimeLibraryOrder = WhisperRuntimeProbe.AutoOrder;
                    try
                    {
                        // The current directory is pointed at the CUDA natives while the model is
                        // opened: that is when ggml hunts for its backend modules, and it scans the
                        // current directory as well as the executable folder.
                        factory = GgmlBackendPath.WithBackendFolder(delegate
                        {
                            return WhisperFactory.FromPath(
                                request.ModelPath,
                                new WhisperFactoryOptions { UseGpu = true });
                        });
                    }
                    catch (Exception exception)
                    {
                        // Missing cuBLAS / cuDNN or an unusable device: fall back instead of failing the job.
                        CrashLog.Write(exception, "SpeechTranscriber.GpuFactory");
                        CrashLog.WriteNote("GPU requested but CUDA failed to initialise: " + WhisperRuntimeProbe.DescribeError(exception));
                        status?.Report(
                            "CUDA could not be initialised (" + WhisperRuntimeProbe.DescribeError(exception)
                            + ") — falling back to CPU.");
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
                    string language = string.IsNullOrWhiteSpace(request.Language) ? "auto" : request.Language;

                    // Every window is a separate piece of work, so several can be decoded at the same
                    // time. Each one needs its own processor: the factory shares the model weights, the
                    // decoder state is per processor. The thread budget is split between them, because
                    // whisper already uses all the threads it is given inside a single decode — running
                    // four decoders on four threads each is not four times faster, it is roughly the
                    // same speed with more memory in play. It wins when the decode is bound by memory
                    // rather than by arithmetic.
                    int requestedWorkers = Math.Max(1, request.ParallelWindows);
                    if (gpuRequested && requestedWorkers > 1)
                    {
                        requestedWorkers = 1;
                        status?.Report("GPU decodes one part at a time — parallel parts only pay off on CPU.");
                    }

                    int usableWindows = 0;
                    for (int index = 0; index < windows.Count; index++)
                    {
                        if (File.Exists(windows[index].WavPath))
                        {
                            usableWindows++;
                        }
                    }

                    int workerCount = Math.Max(1, Math.Min(requestedWorkers, usableWindows));
                    int threadsPerWorker = Math.Max(1, request.Threads / workerCount);
                    WindowProgressTracker tracker = new WindowProgressTracker(progress, windows.Count, workerCount);
                    TranscribeState state = new TranscribeState();
                    object stateGate = new object();

                    if (workerCount > 1)
                    {
                        status?.Report("Transcribing " + windows.Count.ToString(CultureInfo.InvariantCulture)
                            + " parts, " + workerCount.ToString(CultureInfo.InvariantCulture) + " at a time…");
                    }

                    Func<int, WhisperProcessor> createProcessor = delegate (int workerSlot)
                    {
                        WhisperProcessorBuilder workerBuilder = factory.CreateBuilder()
                            .WithThreads(threadsPerWorker)
                            // Whisper otherwise feeds the previous window back in as the initial prompt
                            // and can loop on one sentence for minutes; drop that context.
                            .WithNoContext()
                            .WithLanguage(language)
                            .SplitOnWord()
                            // Token timestamps are what make a two-word cue land on the two words that
                            // were actually spoken. Without them the only timing whisper gives is the
                            // whole segment, and splitting long text has to guess where each piece starts.
                            .WithTokenTimestamps()
                            .WithProgressHandler(delegate (int percent)
                            {
                                tracker.Report(workerSlot, percent);
                            });

                        if (request.TranslateToEnglish)
                        {
                            workerBuilder = workerBuilder.WithTranslate();
                        }

                        if (!string.IsNullOrWhiteSpace(request.InitialPrompt))
                        {
                            workerBuilder = workerBuilder.WithPrompt(request.InitialPrompt!);
                        }

                        return workerBuilder.Build();
                    };

                    ConcurrentQueue<int> pending = new ConcurrentQueue<int>();
                    for (int index = 0; index < windows.Count; index++)
                    {
                        if (File.Exists(windows[index].WavPath))
                        {
                            pending.Enqueue(index);
                        }
                    }

                    // One list per window, stitched together in window order afterwards, so parallel
                    // decoding still produces cues in timeline order no matter which worker finished first.
                    List<List<SubtitleCue>> buckets = new List<List<SubtitleCue>>(windows.Count);
                    for (int index = 0; index < windows.Count; index++)
                    {
                        buckets.Add(new List<SubtitleCue>());
                    }

                    Task[] jobs = new Task[workerCount];
                    for (int slot = 0; slot < workerCount; slot++)
                    {
                        int workerSlot = slot;
                        jobs[slot] = Task.Run(
                            async delegate
                            {
                                // Not a using block: Whisper.net throws "Cannot dispose while processing,
                                // please use DisposeAsync instead" when Dispose() lands while the native
                                // side is still winding down, which is exactly the state pressing Cancel
                                // leaves it in. The async dispose waits for processing to stop first.
                                WhisperProcessor processor = createProcessor(workerSlot);
                                try
                                {
                                    while (pending.TryDequeue(out int windowIndex))
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        tracker.BeginWindow(workerSlot);
                                        await TranscribeWindowAsync(
                                            processor,
                                            windows[windowIndex],
                                            windowIndex + 1,
                                            windows.Count,
                                            request.SkipNonSpeech,
                                            buckets[windowIndex],
                                            state,
                                            stateGate,
                                            status,
                                            cancellationToken).ConfigureAwait(false);
                                        tracker.CompleteWindow(workerSlot);
                                    }
                                }
                                finally
                                {
                                    try
                                    {
                                        await processor.DisposeAsync().ConfigureAwait(false);
                                    }
                                    catch (Exception)
                                    {
                                        // A disposal that still fails must not replace the cancellation
                                        // the caller is waiting for: the model is going away either way.
                                    }
                                }
                            },
                            cancellationToken);
                    }

                    await Task.WhenAll(jobs).ConfigureAwait(false);

                    for (int index = 0; index < buckets.Count; index++)
                    {
                        if (buckets[index].Count > 0)
                        {
                            cues.AddRange(buckets[index]);
                        }
                    }

                    detectedLanguage = state.DetectedLanguage;
                    usedWordTimings = state.UsedWordTimings;
                }

                string libraryClaim = result.RuntimeDescription;
                result.VerifiedBackend = verification.Describe();
                result.VerifiedOnGpu = verification.Kind == WhisperBackendKind.Cuda;

                if (result.VerifiedOnGpu)
                {
                    string videoMemoryDetail;
                    long videoMemory = NvidiaSmiProbe.GetProcessVideoMemory(Environment.ProcessId, out videoMemoryDetail);
                    result.VideoMemoryBytes = videoMemory;

                    if (videoMemory > 0)
                    {
                        result.RuntimeDescription = libraryClaim + " · " + result.VerifiedBackend
                            + " · " + DescribeBytes(videoMemory) + " on the device";
                    }
                    else
                    {
                        result.RuntimeDescription = libraryClaim + " · " + result.VerifiedBackend
                            + " · no device memory visible (" + videoMemoryDetail + ")";
                    }
                }
                else if (verification.Kind == WhisperBackendKind.Cpu)
                {
                    if (gpuRequested)
                    {
                        CrashLog.WriteNote("CUDA build loaded but whisper initialised the CPU backend. "
                            + verification.Evidence);
                        status?.Report(
                            "Heads-up: the CUDA build loaded, but whisper initialised the CPU backend, so the "
                            + "model ran on the CPU. GPU diagnostics says which file it is missing.");
                    }

                    result.RuntimeDescription = libraryClaim + " · " + result.VerifiedBackend;
                }

            }

            stopwatch.Stop();
            // The repeated-line cleanup exists for whisper looping on one sentence, which only
            // happens on whole segments. Word cues are left alone: "no no no" is a real thing to
            // say, and collapsing it would drop two of the three.
            result.Cues = usedWordTimings ? cues : CollapseRepeatedCues(cues);
            result.DetectedLanguage = detectedLanguage;
            result.Elapsed = stopwatch.Elapsed;
            progress?.Report(1.0);
            return result;
        }

        /// <summary>
        /// Decodes one window into the caller's own cue list. Several workers call this at the same
        /// time, so nothing here is shared: the only mutable state it touches is <paramref name="state"/>
        /// and that is written under <paramref name="stateGate"/>.
        /// </summary>
        private static async Task TranscribeWindowAsync(
            WhisperProcessor processor,
            TranscriptionWindow window,
            int windowNumber,
            int windowCount,
            bool skipNonSpeech,
            List<SubtitleCue> destination,
            TranscribeState state,
            object stateGate,
            IProgress<string>? status,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(window.WavPath))
            {
                return;
            }

            if (windowCount > 1)
            {
                status?.Report("Transcribing part " + windowNumber.ToString(CultureInfo.InvariantCulture)
                    + " of " + windowCount.ToString(CultureInfo.InvariantCulture) + "…");
            }

            double windowOffset = window.OffsetSeconds;
            double skipBefore = window.SkipBeforeSeconds;

            using (FileStream waveStream = File.OpenRead(window.WavPath))
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

                    if (skipNonSpeech && IsNonSpeech(text))
                    {
                        continue;
                    }

                    // Overlap between neighbouring windows: the previous window already
                    // transcribed this bit.
                    if (skipBefore > 0.0 && segment.Start.TotalSeconds < skipBefore)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(segment.Language))
                    {
                        lock (stateGate)
                        {
                            if (state.DetectedLanguage.Length == 0)
                            {
                                state.DetectedLanguage = segment.Language;
                            }
                        }
                    }

                    // Whisper gives the words and the time each one was spoken, so every word
                    // becomes its own cue here and the "two to four words per subtitle" part is
                    // decided later against the character budget. A segment with no usable token
                    // timings is still handled the old way: one cue, cut by hand afterwards.
                    List<SubtitleCue> wordCues = BuildWordCues(segment, windowOffset, skipBefore);
                    if (wordCues.Count > 0)
                    {
                        destination.AddRange(wordCues);
                        lock (stateGate)
                        {
                            state.UsedWordTimings = true;
                        }

                        continue;
                    }

                    TimeSpan start = segment.Start + TimeSpan.FromSeconds(windowOffset);
                    TimeSpan end = segment.End + TimeSpan.FromSeconds(windowOffset);
                    if (end <= start)
                    {
                        double estimated = Math.Max(0.4, text.Length * 0.045);
                        end = start + TimeSpan.FromSeconds(estimated);
                    }

                    destination.Add(new SubtitleCue(start, end, text, segment.Probability));
                }
            }
        }

        private static string DescribeBytes(long bytes)
        {
            const double Megabyte = 1024.0 * 1024.0;
            double megabytes = bytes / Megabyte;
            if (megabytes >= 1024.0)
            {
                return (megabytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
            }

            return megabytes.ToString("0", CultureInfo.InvariantCulture) + " MB";
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

        // ── Cue cleanup

        /// <summary>
        /// Whisper can repeat one line for several windows when it loops. A run of consecutive cues that
        /// share the same text, the same length and no audible gap between them becomes a single cue:
        /// the first start is kept and the end is stretched to where the last duplicate ended.
        /// </summary>
        /// <summary>Whisper reports token times in 10 ms steps.</summary>
        private const double CentisecondSeconds = 0.01;

        /// <summary>
        /// One cue per spoken word, timed from whisper's token timestamps. Whisper hands back
        /// sub-word pieces (" She", "llo"), so they are glued back into words between the first
        /// token's start and the last token's end. An empty list means whisper gave no usable
        /// timings and the caller has to fall back to the whole segment.
        /// </summary>
        private static List<SubtitleCue> BuildWordCues(SegmentData segment, double windowOffset, double skipBefore)
        {
            List<SubtitleCue> wordCues = new List<SubtitleCue>();
            WhisperToken[]? tokens = segment.Tokens;
            if (tokens == null || tokens.Length == 0)
            {
                return wordCues;
            }

            StringBuilder text = new StringBuilder();
            long wordStart = 0;
            long wordEnd = 0;
            bool open = false;
            bool anyTiming = false;

            for (int index = 0; index < tokens.Length; index++)
            {
                WhisperToken token = tokens[index];
                string piece = token.Text ?? string.Empty;
                if (piece.Length == 0 || piece[0] == '[')
                {
                    // Special markers such as [_BEG_] and [_TT_120] are not spoken words.
                    continue;
                }

                bool startsWord = !open || piece[0] == ' ';
                if (open && startsWord)
                {
                    AddWord(wordCues, text, wordStart, wordEnd, segment.Probability, windowOffset, skipBefore);
                    text.Clear();
                    open = false;
                }

                if (!open)
                {
                    wordStart = token.Start;
                    open = true;
                }

                wordEnd = token.End;
                if (token.End > token.Start)
                {
                    anyTiming = true;
                }

                text.Append(startsWord ? piece.TrimStart() : piece);
            }

            if (open)
            {
                AddWord(wordCues, text, wordStart, wordEnd, segment.Probability, windowOffset, skipBefore);
            }

            // Timestamps are only filled in when whisper was asked for them; without them every
            // word would land on the same instant, which is worse than one honest segment cue.
            if (!anyTiming || wordCues.Count == 0)
            {
                wordCues.Clear();
            }

            return wordCues;
        }

        private static void AddWord(
            List<SubtitleCue> destination,
            StringBuilder text,
            long startCentiseconds,
            long endCentiseconds,
            float probability,
            double windowOffset,
            double skipBefore)
        {
            string word = text.ToString().Trim();
            if (word.Length == 0)
            {
                return;
            }

            double localStart = startCentiseconds * CentisecondSeconds;
            double localEnd = endCentiseconds * CentisecondSeconds;
            if (localStart + 0.0005 < skipBefore)
            {
                return;
            }

            if (localEnd <= localStart)
            {
                localEnd = localStart + 0.05;
            }

            destination.Add(new SubtitleCue(
                TimeSpan.FromSeconds(localStart + windowOffset),
                TimeSpan.FromSeconds(localEnd + windowOffset),
                word,
                probability));
        }

        private static List<SubtitleCue> CollapseRepeatedCues(List<SubtitleCue> cues)
        {
            if (cues.Count < 2)
            {
                return cues;
            }

            List<SubtitleCue> collapsed = new List<SubtitleCue>(cues.Count);
            SubtitleCue? runStart = null;
            SubtitleCue? lastInRun = null;

            foreach (SubtitleCue cue in cues)
            {
                if (runStart != null && lastInRun != null && IsRepeatedCue(lastInRun, cue))
                {
                    lastInRun = cue;
                    continue;
                }

                if (runStart != null && lastInRun != null)
                {
                    runStart.End = lastInRun.End;
                    collapsed.Add(runStart);
                }

                runStart = cue;
                lastInRun = cue;
            }

            if (runStart != null && lastInRun != null)
            {
                runStart.End = lastInRun.End;
                collapsed.Add(runStart);
            }

            return collapsed;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> is the same line as <paramref name="previous"/>,
        /// starts right after it and lasts about as long.
        /// </summary>
        private static bool IsRepeatedCue(SubtitleCue previous, SubtitleCue candidate)
        {
            string previousText = previous.Text.Trim();
            string candidateText = candidate.Text.Trim();
            if (!string.Equals(previousText, candidateText, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            TimeSpan gap = candidate.Start - previous.End;
            if (gap > DuplicateGapTolerance)
            {
                return false;
            }

            TimeSpan durationDifference = previous.Duration - candidate.Duration;
            return Math.Abs(durationDifference.TotalSeconds) <= DuplicateDurationTolerance.TotalSeconds;
        }

        /// <summary>
        /// The few facts that come out of a window and belong to the whole run, written from
        /// several workers at once.
        /// </summary>
        private sealed class TranscribeState
        {
            public string DetectedLanguage { get; set; } = string.Empty;

            public bool UsedWordTimings { get; set; }
        }

        /// <summary>
        /// Turns "worker 2 is 40 % through its window" into one overall percentage: windows that are
        /// already done plus the fraction each worker is inside right now, over the window count.
        /// Without this the bar would only jump when a whole window finished, or would race
        /// backwards when two workers report out of order.
        /// </summary>
        private sealed class WindowProgressTracker
        {
            private readonly IProgress<double>? progress;
            private readonly double[] windowFractions;
            private readonly int windowCount;
            private readonly object gate = new object();
            private double finishedWindows;

            public WindowProgressTracker(IProgress<double>? progress, int windowCount, int workerCount)
            {
                this.progress = progress;
                this.windowCount = Math.Max(1, windowCount);
                this.windowFractions = new double[Math.Max(1, workerCount)];
            }

            public void BeginWindow(int workerSlot)
            {
                lock (gate)
                {
                    windowFractions[workerSlot] = 0.0;
                }
            }

            public void CompleteWindow(int workerSlot)
            {
                if (progress == null)
                {
                    return;
                }

                double fraction;
                lock (gate)
                {
                    windowFractions[workerSlot] = 0.0;
                    finishedWindows += 1.0;
                    fraction = (finishedWindows + Sum()) / windowCount;
                }

                progress.Report(Clamp(fraction));
            }

            public void Report(int workerSlot, int percent)
            {
                if (progress == null)
                {
                    return;
                }

                double windowFraction = percent / 100.0;
                if (windowFraction < 0.0)
                {
                    windowFraction = 0.0;
                }

                if (windowFraction > 1.0)
                {
                    windowFraction = 1.0;
                }

                double overall;
                lock (gate)
                {
                    // Progress inside one window never runs backwards, even if the native side
                    // reports a smaller number after seeking.
                    if (windowFraction > windowFractions[workerSlot])
                    {
                        windowFractions[workerSlot] = windowFraction;
                    }

                    overall = (finishedWindows + Sum()) / windowCount;
                }

                progress.Report(Clamp(overall));
            }

            private double Sum()
            {
                double total = 0.0;
                for (int index = 0; index < windowFractions.Length; index++)
                {
                    total += windowFractions[index];
                }

                return total;
            }

            private static double Clamp(double value)
            {
                if (value < 0.0)
                {
                    return 0.0;
                }

                if (value > 1.0)
                {
                    return 1.0;
                }

                return value;
            }
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

            // The build ships CPU natives only, so the CUDA whisper library has to be installed next
            // to the app. Without it nothing else can work, so it is checked before the DLLs.
            if (!CudaDetector.IsRuntimePresent(out _))
            {
                reason = "the CUDA whisper runtime is not installed next to the app";
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

        /// <summary>The first line of an exception, short enough for the status bar.</summary>
        public static string DescribeError(Exception exception)
        {
            string message = exception.Message ?? string.Empty;
            int lineBreak = message.IndexOf('\n');
            if (lineBreak > 0)
            {
                message = message.Substring(0, lineBreak);
            }

            message = message.Trim();
            if (message.Length == 0)
            {
                message = exception.GetType().Name;
            }

            return message.Length > 160 ? message.Substring(0, 160) + "…" : message;
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
