using System.ComponentModel;
using System.Threading;

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

        /// <summary>
        /// The token source of a job that can be stopped, or null when it runs to completion. The
        /// popup shows a Cancel button for as long as this is not yet cancelled.
        /// </summary>
        public CancellationTokenSource? Cancellation { get; set; }

        public bool CanCancel
        {
            get
            {
                CancellationTokenSource? source = Cancellation;
                return source != null && !source.IsCancellationRequested;
            }
        }

        /// <summary>Asks the job to stop. Safe to call from the popup button.</summary>
        public void RequestCancel()
        {
            CancellationTokenSource? source = Cancellation;
            if (source == null || source.IsCancellationRequested)
            {
                return;
            }

            source.Cancel();
            Raise(nameof(CanCancel));
        }

        /// <summary>Drops the token source once the job is over, so the button disappears.</summary>
        public void ClearCancellation()
        {
            Cancellation = null;
            Raise(nameof(CanCancel));
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
