using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using SubtitleForge.Models;

namespace SubtitleForge.Services
{
    /// <summary>
    /// One extraction's worth of preview frames plus the source range they cover. The range travels
    /// with the pictures because drawing has to map every frame back to a second.
    /// </summary>
    public sealed class ThumbnailBatch
    {
        public ThumbnailBatch(double sourceStartSeconds, double sourceSpanSeconds, BitmapSource[] frames)
        {
            SourceStartSeconds = sourceStartSeconds;
            SourceSpanSeconds = sourceSpanSeconds;
            Frames = frames;
        }

        public double SourceStartSeconds { get; }

        public double SourceSpanSeconds { get; }

        public BitmapSource[] Frames { get; }

        /// <summary>Seconds of source one frame stands for; smaller means a denser strip.</summary>
        public double SecondsPerFrame
        {
            get
            {
                if (Frames.Length == 0)
                {
                    return 0.0;
                }

                return SourceSpanSeconds / Frames.Length;
            }
        }
    }

    /// <summary>
    /// Remembers the last strip requested per segment. Zooming changes the seconds per frame and
    /// that is the only thing that invalidates a strip: scrolling inside the same zoom keeps the
    /// bitmaps already decoded instead of running ffmpeg again over seconds nobody changed.
    /// </summary>
    public sealed class ThumbnailBatchCache
    {
        private readonly Dictionary<TimelineSegment, ThumbnailBatch> batches = new Dictionary<TimelineSegment, ThumbnailBatch>();

        /// <summary>
        /// Returns the strip already on hand when it was sampled at this density and still covers
        /// every second the segment draws; null means the caller has to extract.
        /// </summary>
        public ThumbnailBatch? Find(
            TimelineSegment segment,
            double sourceStartSeconds,
            double sourceSpanSeconds,
            double secondsPerFrame)
        {
            if (!batches.ContainsKey(segment))
            {
                return null;
            }

            ThumbnailBatch batch = batches[segment];
            if (batch.Frames.Length == 0 || batch.SecondsPerFrame <= 0.0)
            {
                return null;
            }

            // A one-percent band: rounding a pixel width to whole frames must not look like a zoom.
            double tolerance = Math.Max(0.0001, batch.SecondsPerFrame * 0.01);
            if (Math.Abs(batch.SecondsPerFrame - secondsPerFrame) > tolerance)
            {
                return null;
            }

            // A narrower strip would leave a gap where the clip was moved or stretched, so it only
            // counts when it covers the whole range at the density that was asked for.
            if (batch.SourceStartSeconds > sourceStartSeconds + 0.01)
            {
                return null;
            }

            if (batch.SourceStartSeconds + batch.SourceSpanSeconds < sourceStartSeconds + sourceSpanSeconds - 0.01)
            {
                return null;
            }

            return batch;
        }

        public void Store(TimelineSegment segment, ThumbnailBatch batch)
        {
            batches[segment] = batch;
        }

        public void Clear()
        {
            batches.Clear();
        }
    }
}
