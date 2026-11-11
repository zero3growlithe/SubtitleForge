using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using SubtitleForge.Services;

namespace SubtitleForge.Models
{
    /// <summary>
    /// One Whisper model as shown in the model manager list: download size, size on disk
    /// and whether it is present.
    /// </summary>
    public class WhisperModelRow : INotifyPropertyChanged
    {
        private string sizeText = "—";
        private bool isDownloaded;

        public WhisperModelRow(string fileName, string displayName, double approximateMegabytes)
        {
            FileName = fileName;
            DisplayName = displayName;
            ApproximateText = "~" + approximateMegabytes.ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        public string FileName { get; }

        public string DisplayName { get; }

        public string ApproximateText { get; }

        public long SizeBytes { get; private set; }

        public string SizeText
        {
            get { return sizeText; }
            private set
            {
                if (sizeText != value)
                {
                    sizeText = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsDownloaded
        {
            get { return isDownloaded; }
            private set
            {
                if (isDownloaded != value)
                {
                    isDownloaded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string StatusText
        {
            get { return isDownloaded ? "Downloaded" : "Not downloaded"; }
        }

        /// <summary>
        /// Re-reads the file from disk.
        /// </summary>
        public void Refresh()
        {
            SizeBytes = WhisperModelStore.GetModelSizeBytes(FileName);
            IsDownloaded = SizeBytes > 1048576L;
            SizeText = IsDownloaded ? FormatSize(SizeBytes) : "—";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0L)
            {
                return "0 MB";
            }

            double megabytes = bytes / 1048576.0;
            if (megabytes >= 1024.0)
            {
                return (megabytes / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            }

            return megabytes.ToString("0", CultureInfo.InvariantCulture) + " MB";
        }
    }
}
