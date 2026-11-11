using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SubtitleForge.Models
{
    /// <summary>
    /// One row of the multitrack timeline. Audio tracks own 16 kHz mono samples, video
    /// tracks own a set of thumbnails; both are placed on the timeline with a start
    /// offset and can be split without copying media (they share the source arrays and
    /// only differ in offset/length).
    /// </summary>
    public class TimelineTrack : INotifyPropertyChanged
    {
        public const int SampleRate = 16000;

        /// <summary>
        /// Samples per waveform peak bucket (16 ms). Fixed so partial peak updates after
        /// recording stay cheap.
        /// </summary>
        public const int PeakBucketSize = 256;

        private string name;
        private Color backgroundColor;
        private double startSeconds;
        private double sourceOffsetSeconds;
        private double durationSeconds;
        private bool isSelected;

        public TimelineTrack(TrackKind kind, string name, Color backgroundColor)
        {
            Kind = kind;
            this.name = name;
            this.backgroundColor = backgroundColor;
        }

        public TrackKind Kind { get; }

        public string Name
        {
            get { return name; }
            set
            {
                string updated = value ?? string.Empty;
                if (name != updated)
                {
                    name = updated;
                    OnPropertyChanged();
                }
            }
        }

        public Color BackgroundColor
        {
            get { return backgroundColor; }
            set
            {
                if (backgroundColor != value)
                {
                    backgroundColor = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BackgroundBrush));
                    OnPropertyChanged(nameof(WaveformColor));
                    OnPropertyChanged(nameof(WaveformBrush));
                }
            }
        }

        public Brush BackgroundBrush
        {
            get
            {
                SolidColorBrush brush = new SolidColorBrush(backgroundColor);
                brush.Freeze();
                return brush;
            }
        }

        /// <summary>
        /// Waveform colour, derived from the background so it always contrasts.
        /// </summary>
        public Color WaveformColor
        {
            get { return TrackColors.Contrasting(backgroundColor); }
        }

        public Brush WaveformBrush
        {
            get
            {
                SolidColorBrush brush = new SolidColorBrush(WaveformColor);
                brush.Freeze();
                return brush;
            }
        }

        /// <summary>
        /// Where the track begins on the timeline.
        /// </summary>
        public double StartSeconds
        {
            get { return startSeconds; }
            set
            {
                if (Math.Abs(startSeconds - value) > 0.0001)
                {
                    startSeconds = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EndSeconds));
                    OnPropertyChanged(nameof(RangeText));
                }
            }
        }

        /// <summary>
        /// Offset into the source media that <see cref="StartSeconds"/> maps to.
        /// </summary>
        public double SourceOffsetSeconds
        {
            get { return sourceOffsetSeconds; }
            set
            {
                if (Math.Abs(sourceOffsetSeconds - value) > 0.0001)
                {
                    sourceOffsetSeconds = value;
                    OnPropertyChanged();
                }
            }
        }

        public double DurationSeconds
        {
            get { return durationSeconds; }
            set
            {
                double updated = value < 0.0 ? 0.0 : value;
                if (Math.Abs(durationSeconds - updated) > 0.0001)
                {
                    durationSeconds = updated;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EndSeconds));
                    OnPropertyChanged(nameof(RangeText));
                }
            }
        }

        public double EndSeconds
        {
            get { return startSeconds + durationSeconds; }
        }

        public bool IsSelected
        {
            get { return isSelected; }
            set
            {
                if (isSelected != value)
                {
                    isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public string KindText
        {
            get { return Kind == TrackKind.Video ? "V" : "A"; }
        }

        public string RangeText
        {
            get
            {
                return FormatSeconds(startSeconds) + " – " + FormatSeconds(startSeconds + durationSeconds);
            }
        }

        // ── Audio media ────────────────────────────────────────────────────

        /// <summary>
        /// 16 kHz mono samples of the whole source. Read only through this property so a
        /// shared (split) track can be copied before it is written to.
        /// </summary>
        public short[] Samples { get; set; } = Array.Empty<short>();

        /// <summary>
        /// Peak buckets over <see cref="Samples"/>, one entry per <see cref="PeakBucketSize"/> samples.
        /// </summary>
        public float[] Peaks { get; set; } = Array.Empty<float>();

        /// <summary>
        /// True when <see cref="Samples"/> is shared with another track (right after a split).
        /// </summary>
        public bool SharesSamples { get; set; }

        public double SourceDurationSeconds
        {
            get { return Samples.Length / (double)SampleRate; }
        }

        public double SampleIndexToTimeline(int sampleIndex)
        {
            double mediaSeconds = sampleIndex / (double)SampleRate;
            return startSeconds + (mediaSeconds - sourceOffsetSeconds);
        }

        public int TimelineToSampleIndex(double timelineSeconds)
        {
            double mediaSeconds = sourceOffsetSeconds + (timelineSeconds - startSeconds);
            if (mediaSeconds <= 0.0)
            {
                return 0;
            }

            double index = mediaSeconds * SampleRate;
            if (index > int.MaxValue - 2)
            {
                return int.MaxValue - 2;
            }

            return (int)Math.Round(index);
        }

        /// <summary>
        /// Detaches the sample buffer from any track it is shared with, before writing.
        /// </summary>
        public void PrepareForWrite()
        {
            if (!SharesSamples)
            {
                return;
            }

            Samples = (short[])Samples.Clone();
            Peaks = (float[])Peaks.Clone();
            SharesSamples = false;
        }

        /// <summary>
        /// Grows the sample buffer with silence so <paramref name="requiredSamples"/> fit.
        /// </summary>
        public void EnsureCapacity(int requiredSamples)
        {
            if (requiredSamples <= Samples.Length)
            {
                return;
            }

            int newLength = Samples.Length == 0 ? Math.Max(requiredSamples, SampleRate * 5) : Samples.Length;
            while (newLength < requiredSamples)
            {
                newLength = (int)Math.Min(int.MaxValue - 2, Math.Max((long)newLength * 2L, (long)requiredSamples));
            }

            short[] grown = new short[newLength];
            if (Samples.Length > 0)
            {
                Array.Copy(Samples, grown, Samples.Length);
            }

            Samples = grown;

            int bucketCount = PeakBucketCount;
            if (Peaks.Length < bucketCount)
            {
                float[] grownPeaks = new float[bucketCount];
                Array.Copy(Peaks, grownPeaks, Peaks.Length);
                Peaks = grownPeaks;
            }
        }

        public int PeakBucketCount
        {
            get { return (Samples.Length + PeakBucketSize - 1) / PeakBucketSize; }
        }

        public void RebuildPeaks()
        {
            int bucketCount = PeakBucketCount;
            Peaks = new float[bucketCount];
            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                Peaks[bucket] = ComputeBucketPeak(bucket);
            }
        }

        /// <summary>
        /// Recomputes only the buckets touched by a write — what makes live recording redraws cheap.
        /// </summary>
        public void UpdatePeaks(int sampleStart, int sampleCount)
        {
            if (sampleCount <= 0 || Samples.Length == 0)
            {
                return;
            }

            int bucketCount = PeakBucketCount;
            if (Peaks.Length < bucketCount)
            {
                float[] grownPeaks = new float[bucketCount];
                Array.Copy(Peaks, grownPeaks, Peaks.Length);
                Peaks = grownPeaks;
            }

            int firstBucket = Math.Max(0, sampleStart / PeakBucketSize);
            int lastBucket = Math.Min(bucketCount - 1, (sampleStart + sampleCount) / PeakBucketSize);

            for (int bucket = firstBucket; bucket <= lastBucket; bucket++)
            {
                Peaks[bucket] = ComputeBucketPeak(bucket);
            }
        }

        private float ComputeBucketPeak(int bucket)
        {
            int first = bucket * PeakBucketSize;
            int last = Math.Min(Samples.Length, first + PeakBucketSize);
            int maximum = 0;

            for (int index = first; index < last; index++)
            {
                int value = Samples[index];
                if (value < 0)
                {
                    value = -value;
                }

                if (value > maximum)
                {
                    maximum = value;
                }
            }

            return maximum / 32768f;
        }

        // ── Video media ────────────────────────────────────────────────────

        public string? SourcePath { get; set; }

        public BitmapSource[] Thumbnails { get; set; } = Array.Empty<BitmapSource>();

        /// <summary>
        /// Total length of the source media (video tracks only; audio derives it from Samples).
        /// </summary>
        public double SourceDuration { get; set; }

        public double EffectiveSourceDuration
        {
            get
            {
                if (Kind == TrackKind.Audio)
                {
                    return SourceDurationSeconds;
                }

                return SourceDuration > 0.0 ? SourceDuration : SourceOffsetSeconds + DurationSeconds;
            }
        }

        /// <summary>
        /// Splits this track at a timeline position: this instance keeps the head, the
        /// returned track carries the tail. Media is shared, never copied.
        /// </summary>
        public TimelineTrack? SplitAt(double timelineSeconds)
        {
            if (timelineSeconds <= startSeconds + 0.0005 || timelineSeconds >= EndSeconds - 0.0005)
            {
                return null;
            }

            double headDuration = timelineSeconds - startSeconds;

            TimelineTrack tail = new TimelineTrack(Kind, name + " · B", backgroundColor);
            tail.StartSeconds = timelineSeconds;
            tail.SourceOffsetSeconds = sourceOffsetSeconds + headDuration;
            tail.DurationSeconds = durationSeconds - headDuration;
            tail.Samples = Samples;
            tail.Peaks = Peaks;
            tail.SharesSamples = Kind == TrackKind.Audio;
            tail.SourcePath = SourcePath;
            tail.Thumbnails = Thumbnails;
            tail.SourceDuration = SourceDuration;

            DurationSeconds = headDuration;
            return tail;
        }

        public TimelineTrack Clone()
        {
            TimelineTrack copy = new TimelineTrack(Kind, name, backgroundColor);
            copy.StartSeconds = startSeconds;
            copy.SourceOffsetSeconds = sourceOffsetSeconds;
            copy.DurationSeconds = durationSeconds;
            copy.Samples = Samples;
            copy.Peaks = Peaks;
            copy.SharesSamples = SharesSamples;
            copy.SourcePath = SourcePath;
            copy.Thumbnails = Thumbnails;
            copy.SourceDuration = SourceDuration;
            copy.IsSelected = isSelected;
            return copy;
        }

        private static string FormatSeconds(double seconds)
        {
            if (seconds < 0.0)
            {
                seconds = 0.0;
            }

            int minutes = (int)(seconds / 60.0);
            double rest = seconds - (minutes * 60.0);
            return minutes.ToString(CultureInfo.InvariantCulture)
                + ":"
                + rest.ToString("00.0", CultureInfo.InvariantCulture);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
