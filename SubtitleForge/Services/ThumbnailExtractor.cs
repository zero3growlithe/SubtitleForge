using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Pulls an evenly spaced strip of frames out of the video for the video track, in a
    /// single ffmpeg pass, and loads them as frozen bitmaps so the temp files can go away.
    /// </summary>
    public static class ThumbnailExtractor
    {
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
                    Path.Combine(folder, "thumb_%03d.jpg")
                };

                ProcessResult result;
                try
                {
                    result = await ProcessRunner.RunAsync(ffmpegPath, arguments, null, null, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return thumbnails;
                }

                if (result.ExitCode != 0)
                {
                    // Audio-only files simply have no thumbnails.
                    return thumbnails;
                }

                string[] files = Directory.GetFiles(folder, "thumb_*.jpg");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (string file in files)
                {
                    try
                    {
                        BitmapImage bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        bitmap.UriSource = new Uri(file, UriKind.Absolute);
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
    }
}
