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
    /// Caches the audio of a source file as an M4A so the audio lanes can be played without
    /// dragging Media Foundation through the video stream of a 4K file.
    ///
    /// When the source audio is already AAC — which covers everything a phone, a camera or a
    /// screen recorder produces — the audio stream is <em>copied</em>, so what the preview plays is
    /// bit-for-bit the source. Only other codecs (AC-3, PCM, Opus, Vorbis, MP3) are transcoded, and
    /// then at 320 kbps rather than the 192 kbps used before, which was audible on dense material.
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
                bool copyAudio = await IsAacAudioAsync(ffmpegPath, sourcePath, cancellationToken)
                    .ConfigureAwait(false);

                List<string> arguments = new List<string>();
                arguments.Add("-nostdin");
                arguments.Add("-hide_banner");
                arguments.Add("-loglevel");
                arguments.Add("error");
                arguments.Add("-y");
                arguments.Add("-i");
                arguments.Add(sourcePath);
                arguments.Add("-vn");
                arguments.Add("-sn");
                arguments.Add("-dn");

                if (copyAudio)
                {
                    // Nothing is re-encoded: the preview hears the source itself.
                    arguments.Add("-c:a");
                    arguments.Add("copy");
                }
                else
                {
                    arguments.Add("-c:a");
                    arguments.Add("aac");
                    arguments.Add("-b:a");
                    arguments.Add("320k");
                    arguments.Add("-ac");
                    arguments.Add("2");
                    arguments.Add("-ar");
                    arguments.Add("48000");
                }

                arguments.Add("-movflags");
                arguments.Add("+faststart");
                arguments.Add("-threads");
                arguments.Add("2");
                arguments.Add(temporary);

                ProcessResult result = await ProcessRunner
                    .RunAsync(ffmpegPath, arguments, null, null, cancellationToken)
                    .ConfigureAwait(false);

                if (result.ExitCode != 0 || !File.Exists(temporary))
                {
                    TryDelete(temporary);

                    // A copy that ffmpeg refuses (odd sample format, a stream MP4 cannot hold) is
                    // not a reason to lose the cache: transcode it instead.
                    if (copyAudio)
                    {
                        return await TranscodeAsync(ffmpegPath, sourcePath, temporary, target, cancellationToken)
                            .ConfigureAwait(false);
                    }

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

        /// <summary>
        /// True when the first audio stream is AAC, in which case it can be remuxed untouched.
        /// ffmpeg prints the stream list to stderr, and exits non-zero because no output was asked
        /// for — hence the deliberate look at the text only.
        /// </summary>
        private static async Task<bool> IsAacAudioAsync(
            string ffmpegPath,
            string sourcePath,
            CancellationToken cancellationToken)
        {
            try
            {
                List<string> arguments = new List<string> { "-hide_banner", "-nostdin", "-i", sourcePath };

                ProcessResult result = await ProcessRunner
                    .RunAsync(ffmpegPath, arguments, null, null, cancellationToken)
                    .ConfigureAwait(false);

                string text = result.StandardError ?? string.Empty;
                int audio = text.IndexOf("Audio:", StringComparison.OrdinalIgnoreCase);
                if (audio < 0)
                {
                    return false;
                }

                string stream = text.Substring(audio, Math.Min(80, text.Length - audio));
                return stream.IndexOf("aac", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "AudioCodecProbe");
                return false;
            }
        }

        private static async Task<string?> TranscodeAsync(
            string ffmpegPath,
            string sourcePath,
            string temporary,
            string target,
            CancellationToken cancellationToken)
        {
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
                CrashLog.Write(exception, "AudioCacheTranscode");
                TryDelete(temporary);
                return null;
            }
        }

        private static string BuildKey(string sourcePath)
        {
            FileInfo info = new FileInfo(sourcePath);
            // The "aac320" salt is the cache version: bumping it retires files encoded at the
            // older, more lossy bitrate instead of playing them back forever.
            string seed = "audio2|" + sourcePath.ToLowerInvariant()
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
