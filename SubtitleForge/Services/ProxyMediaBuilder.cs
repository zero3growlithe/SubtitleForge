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
        public static async Task<string?> BuildAsync(
            string ffmpegPath,
            string sourcePath,
            VideoMetrics metrics,
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
                return target;
            }

            string temporary = target + ".part.mp4";

            try
            {
                List<string> arguments = new List<string>
                {
                    "-nostdin",
                    "-hide_banner",
                    "-loglevel", "error",
                    "-y",
                    "-i", sourcePath,
                    "-vf", "scale=-2:" + ProxyHeight.ToString(CultureInfo.InvariantCulture),
                    "-c:v", "libx264",
                    "-preset", "veryfast",
                    "-crf", "26",
                    "-pix_fmt", "yuv420p",
                    "-c:a", "aac",
                    "-b:a", "160k",
                    "-ac", "2",
                    "-movflags", "+faststart",
                    temporary
                };

                ProcessResult result = await ProcessRunner
                    .RunAsync(ffmpegPath, arguments, null, null, cancellationToken)
                    .ConfigureAwait(false);

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
