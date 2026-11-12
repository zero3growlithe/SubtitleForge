using System.ComponentModel;

namespace SubtitleForge.Models
{
    /// <summary>One long-running job surfaced in the status bar popup.</summary>
    public sealed class BackgroundTask : INotifyPropertyChanged
    {
        private string title = string.Empty;
        private string detail = string.Empty;
        private double percent;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title
        {
            get
            {
                return title;
            }

            set
            {
                if (title == value)
                {
                    return;
                }

                title = value;
                Raise(nameof(Title));
            }
        }

        public string Detail
        {
            get
            {
                return detail;
            }

            set
            {
                if (detail == value)
                {
                    return;
                }

                detail = value;
                Raise(nameof(Detail));
            }
        }

        /// <summary>True while the job cannot report a percentage yet.</summary>
        public bool IsIndeterminate { get; set; }

        /// <summary>0-100. A negative value means "no measurable progress yet".</summary>
        public double Percent
        {
            get
            {
                return percent;
            }

            set
            {
                if (percent == value)
                {
                    return;
                }

                percent = value;
                Raise(nameof(Percent));
            }
        }

        private void Raise(string propertyName)
        {
            PropertyChangedEventHandler? handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
