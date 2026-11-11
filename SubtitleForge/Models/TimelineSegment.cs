using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace SubtitleForge.Models
{
    /// <summary>
    /// One block of media placed on a track lane. A segment owns its placement (start,
    /// source offset, length) but shares the decoded media arrays with every segment that
    /// came out of a split, so splitting never copies audio or thumbnails.
    /// </summary>
    public class TimelineSegment : INotifyPropertyChanged
    {
        public const int SampleRate = 16000;

        /// <summary>
        /// Samples per waveform peak bucket (16 ms). Fixed so partial peak updates after
        /// recording stay cheap.
        /// </summary>
        public const int PeakBucketSize = 256;

        private double startSeconds;
        private double sourceOffsetSeconds;
        private double durationSeconds;
        private bool isSelected;

        public TimelineSegment(TrackKind kind)
        {
            Kind = kind;
        }

        public TrackKind Kind { get; }

        /// <summary>
        /// Where the segment begins on the timeline.
        /// </summary>
        public double StartSeconds
        {
            get { return startSeconds; }
            set
            {
                double updated = value < 0.0 ? 0.0 : value;
                if (Math.Abs(startSeconds - updated) > 0.0001)
                {
                    startSeconds = updated;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EndSeconds));
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
                double updated = value < 0.0 ? 0.0 : value;
                if (Math.Abs(sourceOffsetSeconds - updated) > 0.0001)
                {
                    sourceOffsetSeconds = updated;
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

        /// <summary>
        /// True when this segment ends before <paramref name="seconds"/> or starts after it.
        /// </summary>
        public bool Contains(double seconds)
        {
            return seconds >= startSeconds && seconds < EndSeconds;
        }

        public bool Overlaps(double start, double end)
        {
            return start < EndSeconds - 0.0005 && end > startSeconds + 0.0005;
        }

        // ── Audio media ────────────────────────────────────────────────────

        /// <summary>
        /// 16 kHz mono samples of the whole source. Written only after <see cref="PrepareForWrite"/>.
        /// </summary>
        public short[] Samples { get; set; } = Array.Empty<short>();

        /// <summary>
        /// Peak buckets over <see cref="Samples"/>, one entry per <see cref="PeakBucketSize"/> samples.
        /// </summary>
        public float[] Peaks { get; set; } = Array.Empty<float>();

        /// <summary>
        /// True when <see cref="Samples"/> is shared with another segment (right after a split).
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
        /// Detaches the sample buffer from any segment it is shared with, before writing.
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

        // ── Source media ───────────────────────────────────────────────────

        /// <summary>
        /// Media file this segment reads from (the video itself, or an imported audio file).
        /// Recorded segments keep this null and carry <see cref="ClipFile"/> instead.
        /// </summary>
        public string? SourcePath { get; set; }

        /// <summary>
        /// File name (relative to the project file) of the exported clip for recorded audio.
        /// </summary>
        public string? ClipFile { get; set; }

        /// <summary>
        /// True when the samples were captured from the microphone rather than decoded from
        /// <see cref="SourcePath"/> — those have to be written out next to the project.
        /// </summary>
        public bool HasRecordedAudio { get; set; }

        public BitmapSource[] Thumbnails { get; set; } = Array.Empty<BitmapSource>();

        /// <summary>
        /// Total length of the source media (video segments; audio derives it from Samples).
        /// </summary>
        public double SourceDuration { get; set; }

        public double EffectiveSourceDuration
        {
            get
            {
                if (Kind == TrackKind.Audio && Samples.Length > 0)
                {
                    return SourceDurationSeconds;
                }

                return SourceDuration > 0.0 ? SourceDuration : SourceOffsetSeconds + DurationSeconds;
            }
        }

        /// <summary>
        /// Splits this segment at a timeline position: this instance keeps the head, the
        /// returned segment carries the tail. Media is shared, never copied.
        /// </summary>
        public TimelineSegment? SplitAt(double timelineSeconds)
        {
            if (timelineSeconds <= startSeconds + 0.0005 || timelineSeconds >= EndSeconds - 0.0005)
            {
                return null;
            }

            double headDuration = timelineSeconds - startSeconds;

            TimelineSegment tail = new TimelineSegment(Kind);
            tail.StartSeconds = timelineSeconds;
            tail.SourceOffsetSeconds = sourceOffsetSeconds + headDuration;
            tail.DurationSeconds = durationSeconds - headDuration;
            tail.Samples = Samples;
            tail.Peaks = Peaks;
            tail.SharesSamples = Kind == TrackKind.Audio;
            tail.SourcePath = SourcePath;
            tail.ClipFile = ClipFile;
            tail.HasRecordedAudio = HasRecordedAudio;
            tail.Thumbnails = Thumbnails;
            tail.SourceDuration = SourceDuration;

            DurationSeconds = headDuration;
            return tail;
        }

        /// <summary>
        /// Copy of the placement that shares every media array with this segment.
        /// </summary>
        public TimelineSegment Clone()
        {
            TimelineSegment copy = new TimelineSegment(Kind);
            copy.StartSeconds = startSeconds;
            copy.SourceOffsetSeconds = sourceOffsetSeconds;
            copy.DurationSeconds = durationSeconds;
            copy.Samples = Samples;
            copy.Peaks = Peaks;
            copy.SharesSamples = SharesSamples || Kind == TrackKind.Audio;
            copy.SourcePath = SourcePath;
            copy.ClipFile = ClipFile;
            copy.HasRecordedAudio = HasRecordedAudio;
            copy.Thumbnails = Thumbnails;
            copy.SourceDuration = SourceDuration;
            copy.IsSelected = isSelected;
            return copy;
        }

        public static string FormatSeconds(double seconds)
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
