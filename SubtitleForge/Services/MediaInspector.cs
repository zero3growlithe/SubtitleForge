using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Reads container media information through ffprobe.
    /// </summary>
    public static class MediaInspector
    {
        /// <summary>
        /// Returns the container duration in seconds, or 0 when it cannot be determined.
        /// </summary>
        public static async Task<double> GetDurationSecondsAsync(
            string ffprobePath,
            string inputPath,
            CancellationToken cancellationToken)
        {
            List<string> arguments = new List<string>
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                inputPath
            };

            ProcessResult result = await ProcessRunner.RunAsync(
                ffprobePath,
                arguments,
                null,
                null,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                return 0.0;
            }

            foreach (string line in result.StandardOutput.Replace("\r", string.Empty).Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                double seconds;
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                    && seconds > 0.0
                    && !double.IsNaN(seconds)
                    && !double.IsInfinity(seconds))
                {
                    return seconds;
                }
            }

            return 0.0;
        }

        /// <summary>
        /// Returns the first video stream's frame rate, or 0 when it cannot be determined.
        /// Fractional rates such as 30000/1001 are honoured.
        /// </summary>
        public static async Task<double> GetFrameRateAsync(
            string ffprobePath,
            string inputPath,
            CancellationToken cancellationToken)
        {
            List<string> arguments = new List<string>
            {
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=r_frame_rate",
                "-of", "default=noprint_wrappers=1:nokey=1",
                inputPath
            };

            ProcessResult result = await ProcessRunner.RunAsync(
                ffprobePath,
                arguments,
                null,
                null,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                return 0.0;
            }

            foreach (string line in result.StandardOutput.Replace("\r", string.Empty).Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                double rate = ParseRate(trimmed);
                if (rate > 0.5 && rate < 480.0)
                {
                    return rate;
                }
            }

            return 0.0;
        }

        private static double ParseRate(string text)
        {
            int slash = text.IndexOf('/');
            if (slash > 0)
            {
                double numerator;
                double denominator;
                if (double.TryParse(text.Substring(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out numerator)
                    && double.TryParse(text.Substring(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out denominator)
                    && denominator > 0.0)
                {
                    return numerator / denominator;
                }

                return 0.0;
            }

            double plain;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out plain))
            {
                return plain;
            }

            return 0.0;
        }
    }
}
