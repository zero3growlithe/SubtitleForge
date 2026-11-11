using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SubtitleForge.Models;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Burns cues into a new MP4 with ffmpeg's subtitles filter.
    /// The .ass file is written into a private temp folder with a fixed name so the
    /// filter argument never has to survive Windows path escaping.
    /// </summary>
    public static class SubtitleBurner
    {
        public static async Task BurnAsync(
            string ffmpegPath,
            string inputPath,
            string outputPath,
            IEnumerable<SubtitleCue> cues,
            AssStyleOptions style,
            string preset,
            int crf,
            double durationSeconds,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            string workingFolder = Path.Combine(
                Path.GetTempPath(),
                "SubtitleForge",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingFolder);

            string subtitleFileName = "subtitles.ass";
            string subtitlePath = Path.Combine(workingFolder, subtitleFileName);
            File.WriteAllText(subtitlePath, SubtitleWriter.ToAss(cues, style), new System.Text.UTF8Encoding(false));

            try
            {
                List<string> arguments = new List<string>
                {
                    "-nostdin",
                    "-hide_banner",
                    "-loglevel", "error",
                    "-y",
                    "-i", inputPath,
                    "-map", "0:v:0",
                    "-map", "0:a?",
                    "-vf", "subtitles=" + subtitleFileName,
                    "-c:v", "libx264",
                    "-preset", string.IsNullOrWhiteSpace(preset) ? "medium" : preset,
                    "-crf", crf.ToString(CultureInfo.InvariantCulture),
                    "-pix_fmt", "yuv420p",
                    "-c:a", "copy",
                    "-movflags", "+faststart",
                    "-progress", "pipe:1",
                    "-nostats",
                    outputPath
                };

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
                    throw new InvalidOperationException("Burning subtitles failed: " + details);
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
    }
}
