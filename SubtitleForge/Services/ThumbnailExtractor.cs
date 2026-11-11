using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Builds the video track's thumbnail strip.
    ///
    /// The first version asked ffmpeg for an even frame rate over the whole file, which decodes
    /// every frame of the movie at full resolution — minutes of work on a long clip. Instead we
    /// fire one short ffmpeg per thumbnail with the seek in front of the input, so ffmpeg jumps
    /// straight to the keyframe before the target and decodes a couple of frames. Those run in
    /// parallel, so the strip appears in about a second even on hour-long footage.
    /// </summary>
    public static class ThumbnailExtractor
    {
        private const int MaxParallelSeeks = 4;

        public static async Task<List<BitmapSource>> ExtractAsync(
            string ffmpegPath,
            string mediaPath,
            double durationSeconds,
            int count,
            int thumbnailHeight,
            CancellationToken cancellationToken)
        {
            List<BitmapSource> thumbnails = new List<BitmapSource>();
            if (count <= 0 || durationSeconds <= 0.5 || thumbnailHeight <= 0)
            {
                return thumbnails;
            }

            string folder = Path.Combine(Path.GetTempPath(), "SubtitleForge", "thumbs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                await ExtractBySeekingAsync(
                    ffmpegPath,
                    mediaPath,
                    durationSeconds,
                    count,
                    thumbnailHeight,
                    folder,
                    cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return thumbnails;
                }

                string[] files = Directory.GetFiles(folder, "thumb_*.jpg");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                if (files.Length == 0)
                {
                    // Seeks can fail on exotic containers; one decoding pass still works.
                    files = await ExtractByScanAsync(
                        ffmpegPath,
                        mediaPath,
                        durationSeconds,
                        count,
                        thumbnailHeight,
                        folder,
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (string file in files)
                {
                    try
                    {
                        BitmapImage bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        bitmap.UriSource = new Uri(file, UriKind.Absolute);
                        bitmap.DecodePixelHeight = thumbnailHeight;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        thumbnails.Add(bitmap);
                    }
                    catch (Exception exception)
                    {
                        CrashLog.Write(exception, "ThumbnailExtractor.Load");
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                    }
                }
                catch (Exception)
                {
                    // Temp cleanup is best effort.
                }
            }

            return thumbnails;
        }

        private static async Task ExtractBySeekingAsync(
            string ffmpegPath,
            string mediaPath,
            double durationSeconds,
            int count,
            int thumbnailHeight,
            string folder,
            CancellationToken cancellationToken)
        {
            using (SemaphoreSlim gate = new SemaphoreSlim(MaxParallelSeeks))
            {
                List<Task> workers = new List<Task>();

                for (int index = 0; index < count; index++)
                {
                    double seconds = durationSeconds * (index + 0.5) / count;
                    string target = Path.Combine(folder, "thumb_" + index.ToString("000", CultureInfo.InvariantCulture) + ".jpg");

                    workers.Add(Task.Run(async delegate
                    {
                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            List<string> arguments = new List<string>
                            {
                                "-nostdin",
                                "-hide_banner",
                                "-loglevel", "error",
                                "-y",
                                "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
                                "-i", mediaPath,
                                "-frames:v", "1",
                                "-an",
                                "-sn",
                                "-threads", "1",
                                "-vf", "scale=-2:" + thumbnailHeight.ToString(CultureInfo.InvariantCulture),
                                "-q:v", "6",
                                target
                            };

                            await ProcessRunner
                                .RunAsync(ffmpegPath, arguments, null, null, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Cancelled — the caller drops the results.
                        }
                        catch (Exception exception)
                        {
                            CrashLog.Write(exception, "ThumbnailSeek");
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }, cancellationToken));
                }

                try
                {
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Nothing to salvage.
                }
            }
        }

        private static async Task<string[]> ExtractByScanAsync(
            string ffmpegPath,
            string mediaPath,
            double durationSeconds,
            int count,
            int thumbnailHeight,
            string folder,
            CancellationToken cancellationToken)
        {
            double framesPerSecond = count / durationSeconds;
            string filter = "fps="
                + framesPerSecond.ToString("0.######", CultureInfo.InvariantCulture)
                + ",scale=-2:"
                + thumbnailHeight.ToString(CultureInfo.InvariantCulture);

            List<string> arguments = new List<string>
            {
                "-nostdin",
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", mediaPath,
                "-vf", filter,
                "-frames:v", count.ToString(CultureInfo.InvariantCulture),
                "-an",
                "-sn",
                Path.Combine(folder, "scan_%03d.jpg")
            };

            try
            {
                ProcessResult result = await ProcessRunner
                    .RunAsync(ffmpegPath, arguments, null, null, cancellationToken)
                    .ConfigureAwait(false);

                if (result.ExitCode != 0)
                {
                    return Array.Empty<string>();
                }
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<string>();
            }

            string[] files = Directory.GetFiles(folder, "scan_*.jpg");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files;
        }
    }
}
