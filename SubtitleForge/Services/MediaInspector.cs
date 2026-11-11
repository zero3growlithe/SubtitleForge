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
    }
}
