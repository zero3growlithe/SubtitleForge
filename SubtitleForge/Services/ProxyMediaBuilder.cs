using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Builds a small H.264 proxy of the source video once, caches it on disk, and lets the
    /// preview and the timeline scrub that instead of the full-resolution original. Export and
    /// burn-in always keep using the original file.
    ///
    /// The proxy is plain H.264/AAC in MP4 because that is what Media Foundation — and therefore
    /// MediaElement — decodes in hardware.
    /// </summary>
    public static class ProxyMediaBuilder
    {
        /// <summary>
        /// Height the preview proxy is rendered at. 540p is plenty for a preview pane and about
        /// six times cheaper to decode and seek than 1080p.
        /// </summary>
        public const int ProxyHeight = 540;

        public static string ProxiesFolder
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SubtitleForge",
                    "proxies");
                return folder;
            }
        }

        /// <summary>
        /// Returns the cached proxy for this file, building it when missing. Null when the file
        /// is already small enough, has no video stream, or ffmpeg could not encode it.
        /// </summary>
        /// <summary>
        /// True when this ffmpeg build carries the NVIDIA hardware H.264 encoder. Probed once per
        /// binary, because nvenc encodes several times faster than libx264 and leaves the CPU to
        /// the preview.
        /// </summary>
        public static async Task<bool> HasHardwareEncoderAsync(string ffmpegPath, CancellationToken cancellationToken)
        {
            lock (EncoderProbes)
            {
                bool known;
                if (EncoderProbes.TryGetValue(ffmpegPath, out known))
                {
                    return known;
                }
            }

            bool available = false;
            try
            {
                List<string> arguments = new List<string> { "-hide_banner", "-encoders" };
                ProcessResult result = await ProcessRunner
                    .RunAsync(ffmpegPath, arguments, null, null, cancellationToken)
                    .ConfigureAwait(false);

                available = result.StandardOutput.IndexOf("h264_nvenc", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "EncoderProbe");
            }

            lock (EncoderProbes)
            {
                EncoderProbes[ffmpegPath] = available;
            }

            return available;
        }

        private static readonly Dictionary<string, bool> EncoderProbes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public static async Task<string?> BuildAsync(
            string ffmpegPath,
            string sourcePath,
            VideoMetrics metrics,
            double durationSeconds,
            bool preferHardware,
            Action<double>? onProgress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            if (!metrics.HasVideo || metrics.Height <= ProxyHeight)
            {
                // Nothing to gain: the original is already cheap to decode and seek.
                return null;
            }

            string folder = ProxiesFolder;
            string? target = null;

            try
            {
                Directory.CreateDirectory(folder);
                target = Path.Combine(folder, BuildKey(sourcePath, metrics.Height) + ".mp4");
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "ProxyFolder");
                return null;
            }

            if (File.Exists(target) && new FileInfo(target).Length > 4096)
            {
                if (onProgress != null)
                {
                    onProgress(1.0);
                }

                return target;
            }

            string temporary = target + ".part.mp4";

            bool hardware = preferHardware && await HasHardwareEncoderAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);

            try
            {
                List<string> arguments = new List<string>
                {
                    "-nostdin",
                    "-hide_banner",
                    "-loglevel", "error",
                    "-progress", "pipe:1",
                    "-nostats",
                    "-y",
                    "-i", sourcePath,
                    "-vf", "scale=-2:" + ProxyHeight.ToString(CultureInfo.InvariantCulture),
                    "-pix_fmt", "yuv420p"
                };

                if (hardware)
                {
                    // nvenc quality tiers are numbered: p1 is fastest, p7 slowest.
                    arguments.Add("-c:v");
                    arguments.Add("h264_nvenc");
                    arguments.Add("-preset");
                    arguments.Add("p2");
                    arguments.Add("-tune");
                    arguments.Add("hq");
                    arguments.Add("-rc");
                    arguments.Add("vbr");
                    arguments.Add("-cq");
                    arguments.Add("26");
                    arguments.Add("-b:v");
                    arguments.Add("0");
                }
                else
                {
                    // Leave cores free: the preview keeps decoding the original while this runs,
                    // and playback must not stutter because the encoder ate the machine.
                    arguments.Add("-c:v");
                    arguments.Add("libx264");
                    arguments.Add("-preset");
                    arguments.Add("veryfast");
                    arguments.Add("-crf");
                    arguments.Add("26");
                    arguments.Add("-threads");
                    arguments.Add("2");
                }

                arguments.Add("-c:a");
                arguments.Add("aac");
                arguments.Add("-b:a");
                arguments.Add("160k");
                arguments.Add("-ac");
                arguments.Add("2");
                arguments.Add("-movflags");
                arguments.Add("+faststart");
                arguments.Add(temporary);

                Action<string> progressHandler = delegate (string line)
                {
                    if (onProgress == null || string.IsNullOrEmpty(line))
                    {
                        return;
                    }

                    double seconds = ParseProgressSeconds(line);
                    if (seconds <= 0.0 || durationSeconds <= 0.0)
                    {
                        return;
                    }

                    double fraction = seconds / durationSeconds;
                    if (fraction > 1.0)
                    {
                        fraction = 1.0;
                    }

                    onProgress(fraction);
                };

                ProcessResult result = await ProcessRunner
                    .RunAsync(ffmpegPath, arguments, null, progressHandler, cancellationToken)
                    .ConfigureAwait(false);

                if ((result.ExitCode != 0 || !File.Exists(temporary)) && hardware)
                {
                    // No usable GPU encoder (driver too old, no CUDA runtime): redo on the CPU.
                    CrashLog.WriteNote("Hardware proxy encode failed, retrying on the CPU: " + result.StandardError);
                    TryDelete(temporary);
                    return await BuildAsync(
                        ffmpegPath,
                        sourcePath,
                        metrics,
                        durationSeconds,
                        false,
                        onProgress,
                        cancellationToken).ConfigureAwait(false);
                }

                if (result.ExitCode != 0 || !File.Exists(temporary))
                {
                    CrashLog.WriteNote("Proxy build failed (" + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " + result.StandardError);
                    TryDelete(temporary);
                    return null;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    TryDelete(temporary);
                    return null;
                }

                File.Move(temporary, target, true);

                if (onProgress != null)
                {
                    onProgress(1.0);
                }

                return target;
            }
            catch (OperationCanceledException)
            {
                TryDelete(temporary);
                return null;
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "ProxyBuild");
                TryDelete(temporary);
                return null;
            }
        }

        /// <summary>
        /// Cache key: path + size + last write, so re-encoding or moving the file invalidates it,
        /// while a rename keeps the proxy usable.
        /// </summary>
        private static string BuildKey(string sourcePath, int sourceHeight)
        {
            FileInfo info = new FileInfo(sourcePath);
            string seed = sourcePath.ToLowerInvariant()
                + "|" + info.Length.ToString(CultureInfo.InvariantCulture)
                + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                + "|" + sourceHeight.ToString(CultureInfo.InvariantCulture)
                + "|" + ProxyHeight.ToString(CultureInfo.InvariantCulture);

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
                StringBuilder text = new StringBuilder(24);
                for (int index = 0; index < 10; index++)
                {
                    text.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
                }

                return "proxy-" + text.ToString();
            }
        }

        /// <summary>
        /// Reads the elapsed output time out of one <c>-progress pipe:1</c> line.
        /// </summary>
        private static double ParseProgressSeconds(string line)
        {
            string trimmed = line.Trim();
            int separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                return 0.0;
            }

            string key = trimmed.Substring(0, separator).Trim();
            string value = trimmed.Substring(separator + 1).Trim();

            double parsed;
            if (string.Equals(key, "out_time_us", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "out_time_ms", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    return 0.0;
                }

                return parsed / 1000000.0;
            }

            if (string.Equals(key, "out_time", StringComparison.OrdinalIgnoreCase))
            {
                TimeSpan stamp;
                if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out stamp))
                {
                    return stamp.TotalSeconds;
                }
            }

            return 0.0;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Cache cleanup is best effort.
            }
        }
    }
}
