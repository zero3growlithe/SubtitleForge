using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SubtitleForge.Models;
using Path = System.IO.Path;

namespace SubtitleForge.Services
{
    /// <summary>What the export window asked for.</summary>
    public sealed class MediaExportOptions
    {
        public bool IncludeVideo { get; set; } = true;

        public bool IncludeAudio { get; set; } = true;

        public bool IncludeSubtitles { get; set; }

        public bool BurnSubtitles { get; set; }

        public string SubtitleFormat { get; set; } = "srt";

        /// <summary>ffmpeg colour expression used as the backdrop when burning without video.</summary>
        public string BackgroundColor { get; set; } = "0x00FF00";

        public string SourcePath { get; set; } = string.Empty;

        public double DurationSeconds { get; set; }

        public int Width { get; set; } = 1920;

        public int Height { get; set; } = 1080;

        /// <summary>
        /// Height of the exported picture. 0 keeps the source size (the project canvas for a
        /// subtitle-only export), anything else scales the picture to that height.
        /// </summary>
        /// <summary>Frame rate for a picture with no source video behind it; 0 follows the source.</summary>
        public double OutputFrameRate { get; set; }

        public int OutputHeight { get; set; }

        /// <summary>
        /// Audio bitrate such as "320k", or "copy" to pass the source audio through untouched.
        /// Changing it forces the audio to be re-encoded.
        /// </summary>
        public string AudioBitrate { get; set; } = "copy";

        /// <summary>Height actually used for the output picture.</summary>
        public int EffectiveHeight
        {
            get
            {
                int source = Height > 0 ? Height : 1080;
                return OutputHeight > 0 ? OutputHeight : source;
            }
        }

        /// <summary>Width that keeps the source aspect ratio and stays even for yuv420p.</summary>
        public int EffectiveWidth
        {
            get
            {
                int sourceWidth = Width > 0 ? Width : 1920;
                int sourceHeight = Height > 0 ? Height : 1080;
                int targetHeight = EffectiveHeight;
                if (targetHeight == sourceHeight)
                {
                    return MakeEven(sourceWidth);
                }

                double scaled = (double)sourceWidth * targetHeight / sourceHeight;
                return MakeEven((int)Math.Round(scaled));
            }
        }

        /// <summary>True when the picture has to be scaled during the export.</summary>
        public bool ResizesPicture
        {
            get
            {
                return OutputHeight > 0 && OutputHeight != (Height > 0 ? Height : 1080);
            }
        }

        /// <summary>True when the audio has to be re-encoded rather than copied.</summary>
        public bool ReEncodesAudio
        {
            get
            {
                return !string.Equals(AudioBitrate, "copy", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Codecs MP4 carries without help. Copying anything else into an MP4 fails, so those get
        /// transcoded instead of failing the export.
        /// </summary>
        public static bool CanCopyIntoMp4(string? codec)
        {
            if (string.IsNullOrEmpty(codec))
            {
                return false;
            }

            string value = codec.Trim().ToLowerInvariant();
            return value == "aac" || value == "mp3" || value == "ac3" || value == "eac3" || value == "alac";
        }

        private static int MakeEven(int value)
        {
            if (value < 2)
            {
                return 2;
            }

            return value % 2 == 0 ? value : value - 1;
        }

        public double FrameRate { get; set; } = 30.0;

        /// <summary>True when anything other than a subtitle sidecar has to be written.</summary>
        public bool NeedsMediaFile
        {
            get
            {
                return IncludeVideo || IncludeAudio || BurnSubtitles;
            }
        }

        /// <summary>True when the output carries a picture (real video or a solid backdrop).</summary>
        public bool ProducesPicture
        {
            get
            {
                return IncludeVideo || BurnSubtitles;
            }
        }
    }

    /// <summary>
    /// Turns the export toggles into one ffmpeg run: remux, subtitle burn-in on the real video,
    /// or subtitle burn-in over a solid colour backdrop that can be keyed out downstream.
    /// </summary>
    public static class MediaExporter
    {
        public static string SuggestedExtension(MediaExportOptions options)
        {
            return options.ProducesPicture ? ".mp4" : ".m4a";
        }

        public static string SuggestedFileName(string baseName, MediaExportOptions options)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(string.IsNullOrWhiteSpace(baseName) ? "export" : baseName);

            if (options.BurnSubtitles && !options.IncludeVideo)
            {
                builder.Append(".subtitles");
            }
            else if (options.BurnSubtitles)
            {
                builder.Append(".subtitled");
            }

            if (!options.IncludeAudio && !options.IncludeVideo && !options.BurnSubtitles)
            {
                builder.Append(".audio");
            }

            builder.Append(SuggestedExtension(options));
            return builder.ToString();
        }

        /// <summary>
        /// Builds the one ffmpeg run that produces the exported file. Burn-in gets the subtitle
        /// frames as a raw BGRA stream on stdin (input 1), everything else is a remux or a plain
        /// scale + re-encode.
        /// </summary>
        public static List<string> BuildArguments(MediaExportOptions options, string inputPath, string outputPath)
        {
            return BuildArguments(options, inputPath, outputPath, true);
        }

        public static List<string> BuildArguments(
            MediaExportOptions options,
            string inputPath,
            string outputPath,
            bool premultipliedAlpha,
            string? audioBitrateOverride = null)
        {
            string audioBitrate = string.IsNullOrEmpty(audioBitrateOverride)
                ? options.AudioBitrate
                : audioBitrateOverride;
            bool reEncodeAudio = !string.Equals(audioBitrate, "copy", StringComparison.OrdinalIgnoreCase);

            List<string> arguments = new List<string>();
            arguments.Add("-nostdin");
            arguments.Add("-hide_banner");
            arguments.Add("-loglevel");
            arguments.Add("error");
            arguments.Add("-y");

            bool solidBackdrop = options.BurnSubtitles && !options.IncludeVideo;
            bool hasSource = !string.IsNullOrEmpty(inputPath);
            bool burnIn = options.BurnSubtitles;

            // Input indices are counted as they are added, because a wrong -map index is the one
            // error that only shows up on one of the export paths.
            int nextInput = 0;
            int audioInput = -1;

            if (solidBackdrop)
            {
                arguments.Add("-f");
                arguments.Add("lavfi");
                arguments.Add("-i");
                arguments.Add(BuildBackdrop(options));
                nextInput = 1;
            }
            else if (hasSource)
            {
                arguments.Add("-i");
                arguments.Add(inputPath);
                audioInput = nextInput;
                nextInput++;
            }

            int rawInput = -1;
            if (burnIn)
            {
                rawInput = nextInput;
                AddRawVideoInput(arguments, options);
                nextInput++;
            }

            // The source is only an input of its own for the backdrop render: the picture comes from
            // lavfi, so the audio still has to be pulled out of the file.
            if (solidBackdrop && hasSource && options.IncludeAudio)
            {
                arguments.Add("-i");
                arguments.Add(inputPath);
                audioInput = nextInput;
                nextInput++;
            }

            // ── Picture ────────────────────────────────────────────────────────
            if (options.ProducesPicture)
            {
                if (burnIn)
                {
                    arguments.Add("-filter_complex");
                    arguments.Add(BuildBurnInFilter(options, solidBackdrop, premultipliedAlpha, rawInput));
                    arguments.Add("-map");
                    arguments.Add("[v]");
                }
                else if (options.ResizesPicture)
                {
                    arguments.Add("-vf");
                    arguments.Add(
                        "scale="
                        + options.EffectiveWidth.ToString(CultureInfo.InvariantCulture)
                        + ":"
                        + options.EffectiveHeight.ToString(CultureInfo.InvariantCulture)
                        + ":flags=bicubic,setsar=1");
                    arguments.Add("-map");
                    arguments.Add("0:v:0");
                }
                else
                {
                    arguments.Add("-map");
                    arguments.Add("0:v:0");
                }
            }
            else
            {
                arguments.Add("-vn");
            }

            // ── Sound ──────────────────────────────────────────────────────────
            if (options.IncludeAudio && audioInput >= 0)
            {
                arguments.Add("-map");
                arguments.Add(audioInput.ToString(CultureInfo.InvariantCulture) + ":a:0?");
            }
            else if (!options.IncludeAudio)
            {
                arguments.Add("-an");
            }

            // ── Codecs ─────────────────────────────────────────────────────────
            bool reEncodeVideo = burnIn || solidBackdrop || !options.IncludeVideo || options.ResizesPicture;

            if (reEncodeVideo)
            {
                arguments.Add("-c:v");
                arguments.Add("libx264");
                arguments.Add("-preset");
                arguments.Add("veryfast");
                arguments.Add("-crf");
                arguments.Add("20");
                arguments.Add("-pix_fmt");
                arguments.Add("yuv420p");
            }
            else
            {
                arguments.Add("-c:v");
                arguments.Add("copy");
            }

            if (options.IncludeAudio && audioInput >= 0)
            {
                if (reEncodeAudio)
                {
                    arguments.Add("-c:a");
                    arguments.Add("aac");
                    arguments.Add("-b:a");
                    arguments.Add(audioBitrate);
                }
                else
                {
                    arguments.Add("-c:a");
                    arguments.Add("copy");
                }
            }

            if (solidBackdrop && options.DurationSeconds > 0.01)
            {
                arguments.Add("-t");
                arguments.Add(options.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }

            arguments.Add("-movflags");
            arguments.Add("+faststart");
            arguments.Add("-progress");
            arguments.Add("pipe:1");
            arguments.Add("-nostats");
            arguments.Add(outputPath);

            return arguments;
        }

        /// <summary>The raw subtitle frames arrive on stdin as one BGRA picture per video frame.</summary>
        private static void AddRawVideoInput(List<string> arguments, MediaExportOptions options)
        {
            arguments.Add("-f");
            arguments.Add("rawvideo");
            arguments.Add("-pix_fmt");
            arguments.Add("bgra");
            arguments.Add("-s");
            arguments.Add(
                options.EffectiveWidth.ToString(CultureInfo.InvariantCulture)
                + "x"
                + options.EffectiveHeight.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-r");
            arguments.Add(BuildFrameRate(options).ToString(CultureInfo.InvariantCulture));
            arguments.Add("-i");
            arguments.Add("pipe:0");
        }

        /// <summary>
        /// The subtitle frames are already premultiplied (that is what WPF renders), so the overlay
        /// filter is told so instead of un-premultiplying every pixel in managed code. After the
        /// last frame the picture simply carries on without subtitles.
        /// </summary>
        private static string BuildBurnInFilter(
            MediaExportOptions options,
            bool solidBackdrop,
            bool premultipliedAlpha,
            int rawInput)
        {
            StringBuilder builder = new StringBuilder();

            if (solidBackdrop)
            {
                // The backdrop is generated at the output size already.
                builder.Append("[0:v]setsar=1[bg];");
            }
            else
            {
                // The picture is always forced to the exact size the subtitle frames are drawn at.
                // Skipping the scale when the sizes happened to be "equal" is what left the overlay
                // covering a quarter of the video whenever the probed size was stale.
                builder.Append("[0:v]scale=");
                builder.Append(options.EffectiveWidth.ToString(CultureInfo.InvariantCulture));
                builder.Append(":");
                builder.Append(options.EffectiveHeight.ToString(CultureInfo.InvariantCulture));
                builder.Append(":flags=bicubic,setsar=1[bg];");
            }

            builder.Append("[bg][");
            builder.Append(rawInput.ToString(CultureInfo.InvariantCulture));
            builder.Append(":v]overlay=0:0");
            if (premultipliedAlpha)
            {
                // WPF hands us premultiplied pixels; telling ffmpeg saves a second pass over every
                // frame. Builds older than 4.3 have no such option, hence the probe.
                builder.Append(":alpha=premultiplied");
            }

            // Blending in RGB keeps the alpha of the subtitle frames; the automatic format can pick a
            // YUV variant that drops it and paints a flat black rectangle instead of the text.
            builder.Append(":format=rgb:eof_action=pass,format=yuv420p[v]");
            return builder.ToString();
        }

        /// <summary>
        /// Remuxes the media, burns the subtitles in through the renderer, or renders them over a
        /// solid backdrop that can be keyed out downstream.
        /// </summary>
        public static async Task ExportAsync(
            string ffmpegPath,
            MediaExportOptions options,
            IReadOnlyList<BurnInCue>? burnInCues,
            string inputPath,
            string outputPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            if (!options.NeedsMediaFile)
            {
                return;
            }

            bool hasSourceFile = !string.IsNullOrEmpty(inputPath) && File.Exists(inputPath);

            bool premultipliedAlpha = true;
            if (options.BurnSubtitles)
            {
                premultipliedAlpha = await SupportsPremultipliedOverlayAsync(ffmpegPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            string? audioBitrate = null;
            if (options.IncludeAudio && !options.ReEncodesAudio && hasSourceFile)
            {
                // "Same as source" only works when MP4 can hold the stream; anything else (Opus,
                // Vorbis, PCM, …) is transcoded rather than failing the whole export.
                string ffprobe = FfmpegLocator.Resolve("ffprobe", Path.GetDirectoryName(ffmpegPath));
                string? codec = await MediaInspector
                    .GetAudioCodecAsync(ffprobe, inputPath, cancellationToken)
                    .ConfigureAwait(false);

                if (!MediaExportOptions.CanCopyIntoMp4(codec))
                {
                    audioBitrate = "320k";
                }
            }

            List<string> arguments = BuildArguments(options, inputPath, outputPath, premultipliedAlpha, audioBitrate);

            if (!options.BurnSubtitles)
            {
                double durationSeconds = options.DurationSeconds;

                Action<string> onLine = delegate (string line)
                {
                    if (progress == null || durationSeconds <= 0.01)
                    {
                        return;
                    }

                    double seconds = AudioTrackExtractor.ParseProgressSeconds(line);
                    if (seconds < 0.0)
                    {
                        return;
                    }

                    double fraction = seconds / durationSeconds;
                    if (fraction < 0.0)
                    {
                        fraction = 0.0;
                    }

                    if (fraction > 1.0)
                    {
                        fraction = 1.0;
                    }

                    progress.Report(fraction);
                };

                ProcessResult result = await ProcessRunner.RunAsync(
                    ffmpegPath,
                    arguments,
                    null,
                    onLine,
                    cancellationToken).ConfigureAwait(false);

                if (result.ExitCode != 0)
                {
                    string details = string.IsNullOrWhiteSpace(result.StandardError)
                        ? "ffmpeg exited with code " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "."
                        : AudioTrackExtractor.FirstMeaningfulLines(result.StandardError);
                    throw new InvalidOperationException("Exporting the media failed: " + details);
                }

                progress?.Report(1.0);
                return;
            }

            // Burn-in: the frames are drawn by the same control the preview uses, so the export
            // carries the profile styling and the idle / pop-in animations, not just plain text.
            await SubtitleBurnInRenderer
                .RunAsync(
                    ffmpegPath,
                    arguments,
                    options.EffectiveWidth,
                    options.EffectiveHeight,
                    BuildFrameRate(options),
                    options.DurationSeconds,
                    burnInCues ?? new List<BurnInCue>(),
                    null,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Asks ffmpeg whether its overlay filter understands premultiplied alpha. Older builds do
        /// not, and then the frames are handed over as they are — the only difference is a slightly
        /// darker edge on semi-transparent glyph borders.
        /// </summary>
        private static async Task<bool> SupportsPremultipliedOverlayAsync(
            string ffmpegPath,
            CancellationToken cancellationToken)
        {
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(
                    ffmpegPath,
                    new List<string> { "-hide_banner", "-h", "filter=overlay" },
                    null,
                    null,
                    cancellationToken).ConfigureAwait(false);

                string text = (result.StandardOutput ?? string.Empty) + (result.StandardError ?? string.Empty);
                return text.IndexOf("premultiplied", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static double BuildFrameRate(MediaExportOptions options)
        {
            // An export without video has no source rate to follow, so the dialog's own choice wins.
            if (options.OutputFrameRate > 1.0 && options.OutputFrameRate <= 120.0)
            {
                return Math.Round(options.OutputFrameRate, 3);
            }

            if (options.FrameRate > 1.0 && options.FrameRate < 240.0)
            {
                return Math.Round(options.FrameRate, 3);
            }

            return 30.0;
        }

        private static string BuildBackdrop(MediaExportOptions options)
        {
            int width = options.EffectiveWidth;
            int height = options.EffectiveHeight;
            // The backdrop has to generate frames at exactly the rate the subtitle frames arrive at,
            // otherwise ffmpeg duplicates or drops them and the animation stutters.
            int frameRate = (int)Math.Round(BuildFrameRate(options));

            StringBuilder builder = new StringBuilder();
            builder.Append("color=c=");
            builder.Append(string.IsNullOrWhiteSpace(options.BackgroundColor) ? "0x00FF00" : options.BackgroundColor);
            builder.Append(":s=");
            builder.Append(width.ToString(CultureInfo.InvariantCulture));
            builder.Append("x");
            builder.Append(height.ToString(CultureInfo.InvariantCulture));
            builder.Append(":r=");
            builder.Append(frameRate.ToString(CultureInfo.InvariantCulture));
            return builder.ToString();
        }

    }
}
