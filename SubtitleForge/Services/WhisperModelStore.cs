using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net.Ggml;

namespace SubtitleForge.Services
{
    public class WhisperModelInfo
    {
        public WhisperModelInfo(GgmlType type, string fileName, string displayName, double approximateMegabytes)
        {
            Type = type;
            FileName = fileName;
            DisplayName = displayName;
            ApproximateMegabytes = approximateMegabytes;
        }

        public GgmlType Type { get; }

        public string FileName { get; }

        public string DisplayName { get; }

        public double ApproximateMegabytes { get; }

        /// <summary>
        /// What the model combo box shows.
        /// </summary>
        public string DisplayText
        {
            get { return DisplayName; }
        }

        /// <summary>
        /// Label plus download size, used by the model manager.
        /// </summary>
        public string DisplayWithSize
        {
            get { return DisplayName + "  (~" + ApproximateMegabytes.ToString("0", CultureInfo.InvariantCulture) + " MB)"; }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>
    /// Keeps ggml Whisper models in %LocalAppData%\SubtitleForge\models,
    /// downloading them on first use through Whisper.net's Hugging Face downloader.
    /// </summary>
    public static class WhisperModelStore
    {
        private static readonly List<WhisperModelInfo> Models = new List<WhisperModelInfo>
        {
            new WhisperModelInfo(GgmlType.Base, "ggml-base.bin", "base — fastest", 142),
            new WhisperModelInfo(GgmlType.Small, "ggml-small.bin", "small — balanced", 466),
            new WhisperModelInfo(GgmlType.Medium, "ggml-medium.bin", "medium — accurate", 1500),
            new WhisperModelInfo(GgmlType.LargeV3, "ggml-large-v3.bin", "large-v3 — best quality", 3100),
            new WhisperModelInfo(GgmlType.LargeV3Turbo, "ggml-large-v3-turbo.bin", "large-v3-turbo — fast + accurate", 1620),
            new WhisperModelInfo(GgmlType.BaseEn, "ggml-base.en.bin", "base.en — English only", 142),
            new WhisperModelInfo(GgmlType.SmallEn, "ggml-small.en.bin", "small.en — English only", 466)
        };

        public static IReadOnlyList<WhisperModelInfo> AvailableModels
        {
            get { return Models; }
        }

        public static string ModelDirectory
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SubtitleForge");
                return Path.Combine(folder, "models");
            }
        }

        public static WhisperModelInfo? Find(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            foreach (WhisperModelInfo model in Models)
            {
                if (string.Equals(model.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return model;
                }
            }

            return null;
        }

        public static string GetModelPath(string fileName)
        {
            return Path.Combine(ModelDirectory, fileName);
        }

        public static bool IsDownloaded(string fileName)
        {
            try
            {
                FileInfo info = new FileInfo(GetModelPath(fileName));
                return info.Exists && info.Length > 1048576L;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// On-disk size of a downloaded model in bytes, or 0 when it is not there.
        /// </summary>
        public static long GetModelSizeBytes(string fileName)
        {
            try
            {
                FileInfo info = new FileInfo(GetModelPath(fileName));
                return info.Exists ? info.Length : 0L;
            }
            catch (Exception)
            {
                return 0L;
            }
        }

        public static bool DeleteModel(string fileName, out string error)
        {
            error = string.Empty;
            try
            {
                string path = GetModelPath(fileName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                // A half-finished download must not survive either.
                string partialPath = path + ".partial";
                if (File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }

                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        /// <summary>
        /// Deletes every downloaded model (and any partial download).
        /// </summary>
        public static int DeleteAllModels(out List<string> failures)
        {
            failures = new List<string>();
            int deleted = 0;

            foreach (WhisperModelInfo model in Models)
            {
                if (!IsDownloaded(model.FileName) && !File.Exists(GetModelPath(model.FileName) + ".partial"))
                {
                    continue;
                }

                string error;
                if (DeleteModel(model.FileName, out error))
                {
                    deleted++;
                }
                else
                {
                    failures.Add(model.DisplayName + ": " + error);
                }
            }

            return deleted;
        }

        /// <summary>
        /// Returns a local path to the model, downloading it first when needed.
        /// </summary>
        public static async Task<string> EnsureModelAsync(
            string fileName,
            IProgress<string>? status,
            IProgress<double>? downloadProgress,
            CancellationToken cancellationToken)
        {
            WhisperModelInfo? model = Find(fileName);
            if (model == null)
            {
                throw new InvalidOperationException("Unknown model: " + fileName);
            }

            string targetPath = GetModelPath(fileName);
            if (IsDownloaded(fileName))
            {
                return targetPath;
            }

            string directory = ModelDirectory;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string partialPath = targetPath + ".partial";
            status?.Report("Downloading " + model.DisplayName + " …");

            try
            {
                using (Stream source = await WhisperGgmlDownloader.Default
                    .GetGgmlModelAsync(model.Type, QuantizationType.NoQuantization, cancellationToken)
                    .ConfigureAwait(false))
                using (FileStream destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[1048576];
                    long totalBytes = (long)(model.ApproximateMegabytes * 1048576.0);
                    long writtenBytes = 0L;
                    int read;

                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        writtenBytes += read;

                        if (downloadProgress != null && totalBytes > 0L)
                        {
                            double fraction = (double)writtenBytes / (double)totalBytes;
                            if (fraction > 0.99)
                            {
                                fraction = 0.99;
                            }

                            downloadProgress.Report(fraction);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }

                FileInfo downloaded = new FileInfo(partialPath);
                if (!downloaded.Exists || downloaded.Length < 1048576L)
                {
                    throw new InvalidOperationException("The downloaded model file is incomplete.");
                }

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(partialPath, targetPath);
                downloadProgress?.Report(1.0);
                status?.Report("Model ready: " + model.DisplayName);
                return targetPath;
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(partialPath))
                    {
                        File.Delete(partialPath);
                    }
                }
                catch (Exception)
                {
                    // Cleanup is best effort.
                }

                throw;
            }
        }
    }
}
