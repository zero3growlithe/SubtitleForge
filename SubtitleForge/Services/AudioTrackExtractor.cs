using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Extracts the first audio stream as 16 kHz mono 16-bit PCM — the only format Whisper accepts.
    /// </summary>
    public static class AudioTrackExtractor
    {
        public static async Task ExtractAsync(
            string ffmpegPath,
            string inputPath,
            string outputWavPath,
            double totalDurationSeconds,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            List<string> arguments = new List<string>
            {
                "-nostdin",
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", inputPath,
                "-map", "0:a:0",
                "-vn",
                "-sn",
                "-dn",
                "-ac", "1",
                "-ar", "16000",
                "-c:a", "pcm_s16le",
                "-progress", "pipe:1",
                "-nostats",
                outputWavPath
            };

            Action<string> onLine = delegate (string line)
            {
                if (progress == null || totalDurationSeconds <= 0.01)
                {
                    return;
                }

                double seconds = ParseProgressSeconds(line);
                if (seconds >= 0.0)
                {
                    double fraction = seconds / totalDurationSeconds;
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
                    : FirstMeaningfulLines(result.StandardError);
                throw new InvalidOperationException("Audio extraction failed: " + details);
            }

            progress?.Report(1.0);
        }

        public static double ParseProgressSeconds(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return -1.0;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                return -1.0;
            }

            string key = line.Substring(0, separator).Trim();
            string value = line.Substring(separator + 1).Trim();
            double parsed;

            if (key == "out_time_us" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed / 1000000.0;
            }

            if (key == "out_time_ms" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                // ffmpeg reports microseconds in out_time_ms despite the name.
                return parsed / 1000000.0;
            }

            if (key == "out_time"
                && TimeSpan.TryParseExact(value, @"hh\:mm\:ss\.ffffff", CultureInfo.InvariantCulture, out TimeSpan timestamp))
            {
                return timestamp.TotalSeconds;
            }

            return -1.0;
        }

        public static string FirstMeaningfulLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string[] lines = text.Replace("\r", string.Empty).Split('\n');
            List<string> meaningful = new List<string>();
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                meaningful.Add(trimmed);
                if (meaningful.Count >= 4)
                {
                    break;
                }
            }

            return string.Join(" | ", meaningful);
        }
    }
}
