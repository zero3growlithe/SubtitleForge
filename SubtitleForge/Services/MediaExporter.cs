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

        public static List<string> BuildArguments(MediaExportOptions options, string subtitleFileName, string inputPath, string outputPath)
        {
            List<string> arguments = new List<string>();
            arguments.Add("-nostdin");
            arguments.Add("-hide_banner");
            arguments.Add("-loglevel");
            arguments.Add("error");
            arguments.Add("-y");

            bool solidBackdrop = options.BurnSubtitles && !options.IncludeVideo;
            bool hasSource = !string.IsNullOrEmpty(inputPath);

            if (solidBackdrop)
            {
                arguments.Add("-f");
                arguments.Add("lavfi");
                arguments.Add("-i");
                arguments.Add(BuildBackdrop(options));

                if (options.IncludeAudio && hasSource)
                {
                    arguments.Add("-i");
                    arguments.Add(inputPath);
                }
            }
            else if (hasSource)
            {
                arguments.Add("-i");
                arguments.Add(inputPath);
            }

            // Picture stream: either the real video, or the solid backdrop.
            if (options.ProducesPicture)
            {
                arguments.Add("-map");
                arguments.Add("0:v:0");
            }
            else
            {
                arguments.Add("-vn");
            }

            if (options.IncludeAudio)
            {
                if (solidBackdrop)
                {
                    if (hasSource)
                    {
                        arguments.Add("-map");
                        arguments.Add("1:a:0?");
                    }
                }
                else if (hasSource)
                {
                    arguments.Add("-map");
                    arguments.Add("0:a:0?");
                }
            }
            else
            {
                arguments.Add("-an");
            }

            bool reEncode = options.BurnSubtitles || solidBackdrop || !options.IncludeVideo;

            if (options.BurnSubtitles)
            {
                string filter = "subtitles=" + QuoteFilterValue(subtitleFileName);
                arguments.Add("-vf");
                arguments.Add(filter);
            }

            if (reEncode)
            {
                arguments.Add("-c:v");
                arguments.Add("libx264");
                arguments.Add("-preset");
                arguments.Add("veryfast");
                arguments.Add("-crf");
                arguments.Add("20");
                arguments.Add("-pix_fmt");
                arguments.Add("yuv420p");

                if (options.IncludeAudio)
                {
                    arguments.Add("-c:a");
                    arguments.Add("aac");
                    arguments.Add("-b:a");
                    arguments.Add("192k");
                }
            }
            else
            {
                arguments.Add("-c");
                arguments.Add("copy");
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

        public static async Task ExportAsync(
            string ffmpegPath,
            MediaExportOptions options,
            IEnumerable<SubtitleCue> cues,
            AssStyleOptions style,
            string inputPath,
            string outputPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            if (!options.NeedsMediaFile)
            {
                return;
            }

            string workingFolder = Path.Combine(
                Path.GetTempPath(),
                "SubtitleForge",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingFolder);

            string subtitleFileName = "subtitles.ass";

            try
            {
                if (options.BurnSubtitles)
                {
                    string subtitlePath = Path.Combine(workingFolder, subtitleFileName);
                    File.WriteAllText(
                        subtitlePath,
                        SubtitleWriter.ToAss(cues, style),
                        new UTF8Encoding(false));
                }

                List<string> arguments = BuildArguments(options, subtitleFileName, inputPath, outputPath);
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
                    workingFolder,
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
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workingFolder))
                    {
                        Directory.Delete(workingFolder, true);
                    }
                }
                catch (Exception)
                {
                    // Temp cleanup is best effort.
                }
            }
        }

        private static string BuildBackdrop(MediaExportOptions options)
        {
            int width = options.Width > 0 ? options.Width : 1920;
            int height = options.Height > 0 ? options.Height : 1080;
            int frameRate = 30;
            if (options.FrameRate > 1.0 && options.FrameRate < 240.0)
            {
                frameRate = (int)Math.Round(options.FrameRate);
            }

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

        private static string QuoteFilterValue(string value)
        {
            return value.Replace("\\", "/").Replace(":", "\\:");
        }
    }
}
