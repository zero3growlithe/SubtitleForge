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
    /// Caches the audio of a source file as a small AAC/M4A so the audio lanes can be played
    /// without dragging Media Foundation through the video stream of a 4K file. The audio-only
    /// transcode is roughly a hundred times faster than real time, so it is ready almost at once.
    /// </summary>
    public static class AudioPlaybackBuilder
    {
        public static string CacheFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SubtitleForge",
                    "audio");
            }
        }

        public static async Task<string?> BuildAsync(
            string ffmpegPath,
            string sourcePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            string folder = CacheFolder;
            string target;

            try
            {
                Directory.CreateDirectory(folder);
                target = Path.Combine(folder, BuildKey(sourcePath) + ".m4a");
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "AudioCacheFolder");
                return null;
            }

            if (File.Exists(target) && new FileInfo(target).Length > 1024)
            {
                return target;
            }

            string temporary = target + ".part.m4a";

            try
            {
                List<string> arguments = new List<string>
                {
                    "-nostdin",
                    "-hide_banner",
                    "-loglevel", "error",
                    "-y",
                    "-i", sourcePath,
                    "-vn",
                    "-sn",
                    "-dn",
                    "-c:a", "aac",
                    // Playback uses this copy, so it is encoded close to transparent rather than
                    // merely small — 192k was audible on dense music.
                    "-b:a", "320k",
                    "-ac", "2",
                    "-ar", "48000",
                    "-movflags", "+faststart",
                    "-threads", "2",
                    temporary
                };

                ProcessResult result = await ProcessRunner
                    .RunAsync(ffmpegPath, arguments, null, null, cancellationToken)
                    .ConfigureAwait(false);

                if (result.ExitCode != 0 || !File.Exists(temporary))
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
                CrashLog.Write(exception, "AudioCacheBuild");
                TryDelete(temporary);
                return null;
            }
        }

        private static string BuildKey(string sourcePath)
        {
            FileInfo info = new FileInfo(sourcePath);
            // The "aac320" salt is the cache version: bumping it retires files encoded at the
            // older, more lossy bitrate instead of playing them back forever.
            string seed = "aac320|" + sourcePath.ToLowerInvariant()
                + "|" + info.Length.ToString(CultureInfo.InvariantCulture)
                + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
                StringBuilder text = new StringBuilder(24);
                for (int index = 0; index < 10; index++)
                {
                    text.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
                }

                return "audio-" + text.ToString();
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
