using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SubtitleForge.Models;

namespace SubtitleForge.Controls
{
    public enum TrackDropKind
    {
        None,
        ExistingTrack,
        NewTrack
    }

    public class TrackMouseDownEventArgs : EventArgs
    {
        public TrackMouseDownEventArgs(TimelineTrack track, bool controlPressed, double seconds, int clickCount)
        {
            Track = track;
            ControlPressed = controlPressed;
            Seconds = seconds;
            ClickCount = clickCount;
        }

        public TimelineTrack Track { get; }

        public bool ControlPressed { get; }

        public double Seconds { get; }

        public int ClickCount { get; }
    }

    public class SegmentMouseDownEventArgs : EventArgs
    {
        public SegmentMouseDownEventArgs(TimelineTrack track, TimelineSegment segment, bool controlPressed, double seconds, int clickCount)
        {
            Track = track;
            Segment = segment;
            ControlPressed = controlPressed;
            Seconds = seconds;
            ClickCount = clickCount;
        }

        public TimelineTrack Track { get; }

        public TimelineSegment Segment { get; }

        public bool ControlPressed { get; }

        public double Seconds { get; }

        public int ClickCount { get; }
    }

    public class SegmentDroppedEventArgs : EventArgs
    {
        public SegmentDroppedEventArgs(
            TimelineSegment segment,
            TimelineTrack sourceTrack,
            TrackDropKind dropKind,
            TimelineTrack? targetTrack,
            int targetRowIndex,
            double newStartSeconds)
        {
            Segment = segment;
            SourceTrack = sourceTrack;
            DropKind = dropKind;
            TargetTrack = targetTrack;
            TargetRowIndex = targetRowIndex;
            NewStartSeconds = newStartSeconds;
        }

        public TimelineSegment Segment { get; }

        public TimelineTrack SourceTrack { get; }

        public TrackDropKind DropKind { get; }

        /// <summary>
        /// Track the segment was dropped on (null when it has to become a new track).
        /// </summary>
        public TimelineTrack? TargetTrack { get; }

        /// <summary>
        /// Row the new track belongs at — also used to report where an existing drop landed.
        /// </summary>
        public int TargetRowIndex { get; }

        public double NewStartSeconds { get; }
    }

    public class AudioFilesDroppedEventArgs : EventArgs
    {
        public AudioFilesDroppedEventArgs(string[] paths, int rowIndex, double seconds)
        {
            Paths = paths;
            RowIndex = rowIndex;
            Seconds = seconds;
        }

        public string[] Paths { get; }

        /// <summary>
        /// Row the files were dropped on, or -1 when they landed on empty space.
        /// </summary>
        public int RowIndex { get; }

        public double Seconds { get; }
    }

    public class TrackReorderEventArgs : EventArgs
    {
        public TrackReorderEventArgs(int fromIndex, int toIndex)
        {
            FromIndex = fromIndex;
            ToIndex = toIndex;
        }

        public int FromIndex { get; }

        public int ToIndex { get; }
    }

    /// <summary>
    /// The multitrack timeline. Each row holds a header strip plus a lane of non-overlapping
    /// segment blocks, drawn in <see cref="OnRender"/> so a thousand peaks cost less than a
    /// thousand elements. Dragging a segment body moves it (snapping against the neighbours
    /// of the lane it is over), dragging a header reorders rows, and dragging the ruler or
    /// empty lane space scrubs the playhead.
    /// </summary>
    public class TrackTimeline : FrameworkElement
    {
        public const double HeaderWidth = 152.0;
        public const double RulerHeight = 22.0;

        /// <summary>
        /// Maps a clip's source file to the fraction of it that already has a playable preview
        /// proxy (1.0 = finished, or no proxy is needed at all). The ruler paints that progress
        /// blue, so it is obvious which stretches of video play smoothly right now.
        /// </summary>
        public Func<string?, double>? SourceReadyFractionProvider { get; set; }

        private static Brush CreateFrozenBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        public const double VideoRowHeight = 66.0;
        public const double AudioRowHeight = 58.0;
        public const double RowGap = 2.0;
        public const double EdgePadding = 2.0;

        private const double SegmentSidePadding = 0.5;
        private const double SegmentCornerRadius = 1.5;
        private const double SnapDistancePixels = 12.0;
        private const double DragThreshold = 4.0;

        private static readonly Typeface HeaderTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface BodyTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Color AccentColor = Color.FromRgb(0x2F, 0x81, 0xF7);
        private static readonly Color SelectionColor = Color.FromRgb(0xF2, 0xF6, 0xFC);
        private static readonly Color RulerColor = Color.FromRgb(0x2A, 0x2A, 0x31);
        private static readonly Brush ReadyBandBrush = CreateFrozenBrush(AccentColor);
        private static readonly Brush BuildingBandBrush = CreateFrozenBrush(Color.FromArgb(0x8C, 0x2F, 0x81, 0xF7));
        private static readonly Color RulerTextColor = Color.FromRgb(0x9A, 0xA0, 0xAC);

        private ObservableCollection<TimelineTrack>? tracks;
        private double totalDuration = 1.0;
        private double playheadSeconds;

        private bool isScrubbing;

        private int rowDragFromIndex = -1;
        private int rowDragInsertIndex = -1;
        private bool isRowDragging;
        private Point rowDragStartPoint;

        private TimelineSegment? dragSegment;
        private TimelineTrack? dragSourceTrack;
        private Point segmentDragStartPoint;
        private bool isSegmentDragging;
        private double segmentDragCandidateStart;
        private double? snapLineSeconds;
        private TrackDropKind segmentDropKind = TrackDropKind.None;
        private TimelineTrack? segmentDropTrack;
        private int segmentDropRowIndex = -1;

        public TrackTimeline()
        {
            SnapsToDevicePixels = true;
            Focusable = true;
            ClipToBounds = true;
            AllowDrop = true;
        }

        public ObservableCollection<TimelineTrack>? Tracks
        {
            get { return tracks; }
            set
            {
                if (ReferenceEquals(tracks, value))
                {
                    return;
                }

                Detach();
                tracks = value;
                Attach();
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        public double TotalDuration
        {
            get { return totalDuration; }
            set
            {
                double updated = value <= 0.0 ? 1.0 : value;
                if (Math.Abs(totalDuration - updated) > 0.0001)
                {
                    totalDuration = updated;
                    InvalidateVisual();
                }
            }
        }

        public double PlayheadSeconds
        {
            get { return playheadSeconds; }
            set
            {
                if (Math.Abs(playheadSeconds - value) > 0.0001)
                {
                    playheadSeconds = value;
                    InvalidateVisual();
                }
            }
        }

        public event EventHandler<TrackMouseDownEventArgs>? TrackMouseDown;

        public event EventHandler<SegmentMouseDownEventArgs>? SegmentMouseDown;

        public event EventHandler<SegmentDroppedEventArgs>? SegmentDropped;

        public event EventHandler<TrackReorderEventArgs>? TrackReorderRequested;

        public event EventHandler<TimelineTrack>? TrackContextMenuRequested;

        public event EventHandler<double>? SeekRequested;

        /// <summary>
        /// Raised when files are dropped from Explorer — they become audio clips.
        /// </summary>
        public event EventHandler<AudioFilesDroppedEventArgs>? AudioFilesDropped;

        public void Refresh()
        {
            InvalidateMeasure();
            InvalidateVisual();
        }

        // ── Layout ─────────────────────────────────────────────────────────

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? 600.0 : availableSize.Width;
            return new Size(width, DesiredHeight());
        }

        private double DesiredHeight()
        {
            double height = RulerHeight + EdgePadding;
            if (tracks == null)
            {
                return height + AudioRowHeight;
            }

            for (int index = 0; index < tracks.Count; index++)
            {
                height += RowHeight(tracks[index]) + RowGap;
            }

            return Math.Max(height, RulerHeight + AudioRowHeight);
        }

        private static double RowHeight(TimelineTrack track)
        {
            return track.Kind == TrackKind.Video ? VideoRowHeight : AudioRowHeight;
        }

        private double ContentLeft
        {
            get { return HeaderWidth + 4.0; }
        }

        private double ContentWidth
        {
            get { return Math.Max(1.0, ActualWidth - ContentLeft - 4.0); }
        }

        private double RowTop(int index)
        {
            double top = RulerHeight + EdgePadding;
            if (tracks == null)
            {
                return top;
            }

            for (int row = 0; row < index && row < tracks.Count; row++)
            {
                top += RowHeight(tracks[row]) + RowGap;
            }

            return top;
        }

        private int RowAt(Point point)
        {
            if (tracks == null)
            {
                return -1;
            }

            for (int index = 0; index < tracks.Count; index++)
            {
                double top = RowTop(index);
                double bottom = top + RowHeight(tracks[index]);
                if (point.Y >= top && point.Y <= bottom)
                {
                    return index;
                }
            }

            return -1;
        }

        private int InsertIndexAt(double y)
        {
            if (tracks == null || tracks.Count == 0)
            {
                return 0;
            }

            for (int index = 0; index < tracks.Count; index++)
            {
                double top = RowTop(index);
                double bottom = top + RowHeight(tracks[index]);
                double middle = top + ((bottom - top) / 2.0);
                if (y < middle)
                {
                    return index;
                }
            }

            return tracks.Count;
        }

        private double SecondsAt(Point point)
        {
            double fraction = (point.X - ContentLeft) / ContentWidth;
            if (fraction < 0.0)
            {
                fraction = 0.0;
            }

            if (fraction > 1.0)
            {
                fraction = 1.0;
            }

            return fraction * totalDuration;
        }

        private double XForSeconds(double seconds)
        {
            if (totalDuration <= 0.0)
            {
                return ContentLeft;
            }

            double x = ContentLeft + (ContentWidth * (seconds / totalDuration));
            if (x < ContentLeft)
            {
                x = ContentLeft;
            }

            return x;
        }

        private double PixelsPerSecond
        {
            get { return totalDuration <= 0.0 ? 1.0 : ContentWidth / totalDuration; }
        }

        // ── Rendering ──────────────────────────────────────────────────────

        protected override void OnRender(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            DrawRuler(drawingContext);

            if (tracks == null || tracks.Count == 0)
            {
                DrawCenteredHint(drawingContext, "Open a video to build the timeline");
                return;
            }

            for (int index = 0; index < tracks.Count; index++)
            {
                DrawTrack(drawingContext, tracks[index], index);
            }

            if (isRowDragging && rowDragInsertIndex >= 0)
            {
                double y = rowDragInsertIndex >= tracks.Count
                    ? RowTop(tracks.Count - 1) + RowHeight(tracks[tracks.Count - 1])
                    : RowTop(rowDragInsertIndex) - (RowGap / 2.0);
                Pen pen = new Pen(new SolidColorBrush(AccentColor), 2.0);
                drawingContext.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
            }

            if (isSegmentDragging)
            {
                DrawSegmentDragPreview(drawingContext);
            }

            if (snapLineSeconds.HasValue)
            {
                double snapX = XForSeconds(snapLineSeconds.Value);
                Pen snapPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66)), 1.5);
                drawingContext.DrawLine(snapPen, new Point(snapX, RulerHeight), new Point(snapX, ActualHeight));
            }

            double playheadX = XForSeconds(playheadSeconds);
            Pen playheadPen = new Pen(new SolidColorBrush(AccentColor), 1.5);
            drawingContext.DrawLine(playheadPen, new Point(playheadX, 0), new Point(playheadX, ActualHeight));
        }

        private void DrawRuler(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(new SolidColorBrush(RulerColor), null, new Rect(0, 0, ActualWidth, RulerHeight));

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Brush textBrush = new SolidColorBrush(RulerTextColor);
            Pen tickPen = new Pen(new SolidColorBrush(Color.FromRgb(0x53, 0x53, 0x5F)), 1.0);

            double step = ChooseTickStep();
            if (step <= 0.0)
            {
                return;
            }

            double seconds = 0.0;
            while (seconds <= totalDuration + 0.0001)
            {
                double x = XForSeconds(seconds);
                bool major = Math.Abs(seconds % (step * 5.0)) < 0.001 || Math.Abs(seconds) < 0.001;
                double tickHeight = major ? RulerHeight - 8.0 : RulerHeight - 4.0;

                drawingContext.DrawLine(tickPen, new Point(x, tickHeight), new Point(x, RulerHeight));

                if (major)
                {
                    FormattedText label = new FormattedText(
                        FormatClock(seconds),
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        BodyTypeface,
                        9.5,
                        textBrush,
                        pixelsPerDip);
                    drawingContext.DrawText(label, new Point(x + 3.0, 2.0));
                }

                seconds += step;
            }

            DrawReadyBand(drawingContext);
        }

        private void DrawReadyBand(DrawingContext drawingContext)
        {
            Func<string?, double>? provider = SourceReadyFractionProvider;
            if (provider == null || tracks == null || tracks.Count == 0)
            {
                return;
            }

            double bandHeight = 4.0;
            double bandTop = RulerHeight - bandHeight;

            foreach (TimelineTrack track in tracks)
            {
                if (track.Kind != TrackKind.Video)
                {
                    continue;
                }

                foreach (TimelineSegment segment in track.Segments)
                {
                    double fraction = provider(segment.SourcePath);
                    if (fraction <= 0.0)
                    {
                        continue;
                    }

                    // Ready source range is [0, fraction * sourceDuration]; clip it to the slice
                    // of the source this segment actually shows.
                    double beginSource = segment.SourceOffsetSeconds;
                    double endSource = segment.SourceOffsetSeconds + segment.DurationSeconds;

                    if (fraction < 0.999)
                    {
                        double readyEnd = fraction * segment.EffectiveSourceDuration;
                        if (readyEnd < endSource)
                        {
                            endSource = readyEnd;
                        }
                    }

                    if (endSource <= beginSource + 0.0005)
                    {
                        continue;
                    }

                    double startTimeline = segment.StartSeconds + (beginSource - segment.SourceOffsetSeconds);
                    double endTimeline = segment.StartSeconds + (endSource - segment.SourceOffsetSeconds);
                    double left = XForSeconds(startTimeline);
                    double right = XForSeconds(endTimeline);

                    if (right <= left)
                    {
                        continue;
                    }

                    Brush brush = fraction >= 0.999 ? ReadyBandBrush : BuildingBandBrush;
                    drawingContext.DrawRectangle(brush, null, new Rect(left, bandTop, right - left, bandHeight));
                }
            }
        }

        private double ChooseTickStep()
        {
            double[] candidates = { 0.5, 1.0, 2.0, 5.0, 10.0, 15.0, 30.0, 60.0, 120.0, 300.0, 600.0 };
            foreach (double candidate in candidates)
            {
                if (candidate * PixelsPerSecond >= 22.0)
                {
                    return candidate;
                }
            }

            return candidates[candidates.Length - 1];
        }

        private static string FormatClock(double seconds)
        {
            int totalSeconds = (int)Math.Round(seconds);
            int minutes = totalSeconds / 60;
            int rest = totalSeconds % 60;
            return minutes.ToString(CultureInfo.InvariantCulture)
                + ":"
                + rest.ToString("00", CultureInfo.InvariantCulture);
        }

        private void DrawTrack(DrawingContext drawingContext, TimelineTrack track, int index)
        {
            double top = RowTop(index);
            double height = RowHeight(track);

            drawingContext.DrawRectangle(track.LaneBrush, null, new Rect(0, top, ActualWidth, height));

            Rect headerRect = new Rect(4.0, top, HeaderWidth, height);
            DrawHeader(drawingContext, track, headerRect);

            if (track.IsSelected)
            {
                Pen selectionPen = new Pen(new SolidColorBrush(SelectionColor), 2.0);
                drawingContext.DrawRoundedRectangle(null, selectionPen, new Rect(1.0, top + 1.0, ActualWidth - 2.0, height - 2.0), 4.0, 4.0);
            }

            foreach (TimelineSegment segment in track.Segments)
            {
                DrawSegment(drawingContext, track, segment, top, height);
            }

            if (!track.HasSegments)
            {
                double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                FormattedText hint = new FormattedText(
                    "empty lane — drag a clip here, or select the track and press Record",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    BodyTypeface,
                    11.0,
                    track.WaveformBrush,
                    pixelsPerDip);
                double hintX = Math.Max(ContentLeft + 8.0, HeaderWidth + 8.0);
                drawingContext.DrawText(hint, new Point(hintX, top + (height / 2.0) - 8.0));
            }
        }

        private void DrawHeader(DrawingContext drawingContext, TimelineTrack track, Rect headerRect)
        {
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Brush textBrush = track.WaveformBrush;

            FormattedText kindText = new FormattedText(
                track.KindText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                HeaderTypeface,
                11.0,
                textBrush,
                pixelsPerDip);
            drawingContext.DrawText(kindText, new Point(headerRect.X + 6.0, headerRect.Y + 5.0));

            FormattedText nameText = new FormattedText(
                track.Name,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                HeaderTypeface,
                12.5,
                textBrush,
                pixelsPerDip);
            nameText.MaxTextWidth = HeaderWidth - 34.0;
            nameText.MaxLineCount = 1;
            nameText.Trimming = TextTrimming.CharacterEllipsis;
            drawingContext.DrawText(nameText, new Point(headerRect.X + 22.0, headerRect.Y + 3.0));

            FormattedText rangeText = new FormattedText(
                track.RangeText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                BodyTypeface,
                10.5,
                textBrush,
                pixelsPerDip);
            rangeText.MaxTextWidth = HeaderWidth - 10.0;
            rangeText.MaxLineCount = 1;
            rangeText.Trimming = TextTrimming.CharacterEllipsis;
            drawingContext.DrawText(rangeText, new Point(headerRect.X + 6.0, headerRect.Y + headerRect.Height - 17.0));
        }

        private void DrawSegment(DrawingContext drawingContext, TimelineTrack track, TimelineSegment segment, double rowTop, double rowHeight)
        {
            Rect rect = SegmentRect(segment, rowTop, rowHeight);
            if (rect.Width <= 0.5)
            {
                return;
            }

            drawingContext.DrawRoundedRectangle(track.BackgroundBrush, null, rect, SegmentCornerRadius, SegmentCornerRadius);

            drawingContext.PushClip(new RectangleGeometry(rect));
            try
            {
                if (segment.Kind == TrackKind.Video)
                {
                    DrawThumbnails(drawingContext, track, segment, rect);
                }
                else
                {
                    DrawWaveform(drawingContext, track, segment, rect);
                }
            }
            finally
            {
                drawingContext.Pop();
            }

            if (segment.IsSelected)
            {
                Pen selectionPen = new Pen(new SolidColorBrush(SelectionColor), 2.0);
                drawingContext.DrawRoundedRectangle(null, selectionPen, new Rect(rect.X + 1.0, rect.Y + 1.0, rect.Width - 2.0, rect.Height - 2.0), SegmentCornerRadius, SegmentCornerRadius);
            }
        }

        private Rect SegmentRect(TimelineSegment segment, double rowTop, double rowHeight)
        {
            double left = XForSeconds(segment.StartSeconds) + SegmentSidePadding;
            double right = XForSeconds(segment.EndSeconds) - SegmentSidePadding;
            double width = Math.Max(0.0, right - left);
            return new Rect(left, rowTop + 1.0, width, rowHeight - 2.0);
        }

        private void DrawWaveform(DrawingContext drawingContext, TimelineTrack track, TimelineSegment segment, Rect rect)
        {
            if (segment.Samples.Length == 0)
            {
                return;
            }

            int pixelCount = (int)Math.Round(rect.Width);
            if (pixelCount < 2 || segment.DurationSeconds <= 0.0)
            {
                return;
            }

            double centerY = rect.Y + (rect.Height / 2.0);
            double halfHeight = Math.Max(2.0, (rect.Height / 2.0) - 3.0);
            double samplesPerPixel = Math.Max(1.0, (segment.DurationSeconds * TimelineSegment.SampleRate) / pixelCount);

            double[] tops = new double[pixelCount];
            for (int pixel = 0; pixel < pixelCount; pixel++)
            {
                double timelineSeconds = segment.StartSeconds + (segment.DurationSeconds * ((pixel + 0.5) / pixelCount));
                double mediaSeconds = segment.SourceOffsetSeconds + (timelineSeconds - segment.StartSeconds);
                double peak = PeakFor(segment, mediaSeconds, samplesPerPixel);
                tops[pixel] = centerY - Math.Max(0.6, peak * halfHeight);
            }

            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(new Point(rect.X, centerY), true, true);
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    context.LineTo(new Point(rect.X + pixel, tops[pixel]), false, false);
                }

                for (int pixel = pixelCount - 1; pixel >= 0; pixel--)
                {
                    context.LineTo(new Point(rect.X + pixel, centerY + (centerY - tops[pixel])), false, false);
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(track.WaveformBrush, null, geometry);
        }

        private static double PeakFor(TimelineSegment segment, double mediaSeconds, double samplesPerPixel)
        {
            if (mediaSeconds < 0.0)
            {
                return 0.0;
            }

            int sampleIndex = (int)(mediaSeconds * TimelineSegment.SampleRate);
            if (sampleIndex >= segment.Samples.Length || segment.Peaks.Length == 0)
            {
                return 0.0;
            }

            int firstBucket = Math.Max(0, sampleIndex / TimelineSegment.PeakBucketSize);
            int lastBucket = Math.Min(segment.Peaks.Length - 1, (int)((sampleIndex + samplesPerPixel) / TimelineSegment.PeakBucketSize));

            float peak = 0f;
            for (int bucket = firstBucket; bucket <= lastBucket; bucket++)
            {
                if (segment.Peaks[bucket] > peak)
                {
                    peak = segment.Peaks[bucket];
                }
            }

            return peak;
        }

        private void DrawThumbnails(DrawingContext drawingContext, TimelineTrack track, TimelineSegment segment, Rect rect)
        {
            if (segment.Thumbnails.Length == 0 || segment.DurationSeconds <= 0.0)
            {
                return;
            }

            double sourceDuration = segment.EffectiveSourceDuration;
            if (sourceDuration <= 0.0)
            {
                return;
            }

            int count = segment.Thumbnails.Length;
            for (int index = 0; index < count; index++)
            {
                double thumbnailStart = sourceDuration * index / count;
                double thumbnailEnd = sourceDuration * (index + 1) / count;

                double visibleStart = Math.Max(thumbnailStart, segment.SourceOffsetSeconds);
                double visibleEnd = Math.Min(thumbnailEnd, segment.SourceOffsetSeconds + segment.DurationSeconds);
                if (visibleEnd <= visibleStart)
                {
                    continue;
                }

                double startFraction = (visibleStart - segment.SourceOffsetSeconds) / segment.DurationSeconds;
                double endFraction = (visibleEnd - segment.SourceOffsetSeconds) / segment.DurationSeconds;

                double x = rect.X + (rect.Width * startFraction);
                double width = rect.Width * (endFraction - startFraction);
                if (width <= 0.5)
                {
                    continue;
                }

                drawingContext.DrawImage(segment.Thumbnails[index], new Rect(x, rect.Y, width, rect.Height));
                Pen divider = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1.0);
                drawingContext.DrawLine(divider, new Point(x, rect.Y), new Point(x, rect.Y + rect.Height));
            }
        }

        private void DrawSegmentDragPreview(DrawingContext drawingContext)
        {
            if (dragSegment == null)
            {
                return;
            }

            double rowTop;
            double rowHeight;
            if (segmentDropKind == TrackDropKind.ExistingTrack && segmentDropTrack != null && segmentDropRowIndex >= 0)
            {
                rowTop = RowTop(segmentDropRowIndex);
                rowHeight = RowHeight(segmentDropTrack);
            }
            else if (segmentDropKind == TrackDropKind.NewTrack)
            {
                double insertionY = segmentDropRowIndex >= 0 && tracks != null && segmentDropRowIndex < tracks.Count
                    ? RowTop(segmentDropRowIndex)
                    : (tracks != null && tracks.Count > 0 ? RowTop(tracks.Count - 1) + RowHeight(tracks[tracks.Count - 1]) : RulerHeight + EdgePadding);
                Pen gapPen = new Pen(new SolidColorBrush(AccentColor), 2.0);
                drawingContext.DrawLine(gapPen, new Point(0, insertionY), new Point(ActualWidth, insertionY));
                rowTop = insertionY + RowGap;
                rowHeight = AudioRowHeight;
            }
            else
            {
                return;
            }

            double left = XForSeconds(segmentDragCandidateStart) + SegmentSidePadding;
            double right = XForSeconds(segmentDragCandidateStart + dragSegment.DurationSeconds) - SegmentSidePadding;
            double width = Math.Max(2.0, right - left);

            Rect preview = new Rect(left, rowTop + 3.0, width, rowHeight - 6.0);
            Brush fill = new SolidColorBrush(Color.FromArgb(190, 0x2F, 0x81, 0xF7));
            Pen outline = new Pen(new SolidColorBrush(Colors.White), 1.5);
            drawingContext.DrawRoundedRectangle(fill, outline, preview, 3.0, 3.0);
        }

        private void DrawCenteredHint(DrawingContext drawingContext, string text)
        {
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText formatted = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                BodyTypeface,
                12.0,
                new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6)),
                pixelsPerDip);

            drawingContext.DrawText(
                formatted,
                new Point((ActualWidth - formatted.Width) / 2.0, (ActualHeight - formatted.Height) / 2.0));
        }

        // ── Hit testing ────────────────────────────────────────────────────

        private TimelineSegment? SegmentAt(TimelineTrack track, Point point, out int rowIndex)
        {
            rowIndex = RowAt(point);
            if (rowIndex < 0)
            {
                return null;
            }

            double rowTop = RowTop(rowIndex);
            double rowHeight = RowHeight(track);
            foreach (TimelineSegment segment in track.Segments)
            {
                Rect rect = SegmentRect(segment, rowTop, rowHeight);
                if (point.X >= rect.X && point.X <= rect.X + rect.Width)
                {
                    return segment;
                }
            }

            return null;
        }

        // ── Mouse ──────────────────────────────────────────────────────────

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();

            Point point = e.GetPosition(this);

            if (point.Y <= RulerHeight)
            {
                isScrubbing = true;
                CaptureMouse();
                SeekRequested?.Invoke(this, SecondsAt(point));
                e.Handled = true;
                return;
            }

            int rowIndex = RowAt(point);
            if (tracks == null || rowIndex < 0)
            {
                SeekRequested?.Invoke(this, SecondsAt(point));
                e.Handled = true;
                return;
            }

            TimelineTrack track = tracks[rowIndex];
            bool controlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (point.X < HeaderWidth)
            {
                TrackMouseDown?.Invoke(this, new TrackMouseDownEventArgs(track, controlPressed, SecondsAt(point), e.ClickCount));

                rowDragFromIndex = rowIndex;
                rowDragInsertIndex = -1;
                rowDragStartPoint = point;
                isRowDragging = false;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            int segmentRow;
            TimelineSegment? segment = SegmentAt(track, point, out segmentRow);
            if (segment == null)
            {
                isScrubbing = true;
                CaptureMouse();
                SeekRequested?.Invoke(this, SecondsAt(point));
                e.Handled = true;
                return;
            }

            SegmentMouseDown?.Invoke(this, new SegmentMouseDownEventArgs(track, segment, controlPressed, SecondsAt(point), e.ClickCount));

            if (shiftPressed)
            {
                isScrubbing = true;
                CaptureMouse();
                SeekRequested?.Invoke(this, SecondsAt(point));
                e.Handled = true;
                return;
            }

            dragSegment = segment;
            dragSourceTrack = track;
            segmentDragStartPoint = point;
            segmentDragCandidateStart = segment.StartSeconds;
            isSegmentDragging = false;
            snapLineSeconds = null;
            segmentDropKind = TrackDropKind.None;
            segmentDropTrack = null;
            segmentDropRowIndex = -1;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            Point point = e.GetPosition(this);

            if (isScrubbing)
            {
                SeekRequested?.Invoke(this, SecondsAt(point));
                return;
            }

            if (rowDragFromIndex >= 0 && e.LeftButton == MouseButtonState.Pressed)
            {
                if (!isRowDragging && Math.Abs(point.Y - rowDragStartPoint.Y) < DragThreshold)
                {
                    return;
                }

                isRowDragging = true;
                rowDragInsertIndex = InsertIndexAt(point.Y);
                InvalidateVisual();
                return;
            }

            if (dragSegment != null && e.LeftButton == MouseButtonState.Pressed)
            {
                if (!isSegmentDragging
                    && Math.Abs(point.X - segmentDragStartPoint.X) < DragThreshold
                    && Math.Abs(point.Y - segmentDragStartPoint.Y) < DragThreshold)
                {
                    return;
                }

                isSegmentDragging = true;
                UpdateSegmentDrag(point);
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            if (isScrubbing)
            {
                isScrubbing = false;
                if (IsMouseCaptured)
                {
                    ReleaseMouseCapture();
                }
            }

            if (rowDragFromIndex >= 0)
            {
                int fromIndex = rowDragFromIndex;
                int toIndex = rowDragInsertIndex;
                bool moved = isRowDragging;

                rowDragFromIndex = -1;
                rowDragInsertIndex = -1;
                isRowDragging = false;

                if (moved && toIndex >= 0)
                {
                    TrackReorderRequested?.Invoke(this, new TrackReorderEventArgs(fromIndex, toIndex));
                }

                if (IsMouseCaptured)
                {
                    ReleaseMouseCapture();
                }

                InvalidateVisual();
            }

            if (dragSegment != null)
            {
                TimelineSegment segment = dragSegment;
                TimelineTrack? sourceTrack = dragSourceTrack;
                bool dragged = isSegmentDragging;
                TrackDropKind dropKind = segmentDropKind;
                TimelineTrack? dropTrack = segmentDropTrack;
                int dropRow = segmentDropRowIndex;
                double start = segmentDragCandidateStart;

                dragSegment = null;
                dragSourceTrack = null;
                isSegmentDragging = false;
                snapLineSeconds = null;
                segmentDropKind = TrackDropKind.None;
                segmentDropTrack = null;
                segmentDropRowIndex = -1;

                if (IsMouseCaptured)
                {
                    ReleaseMouseCapture();
                }

                if (dragged && dropKind != TrackDropKind.None && sourceTrack != null)
                {
                    SegmentDropped?.Invoke(
                        this,
                        new SegmentDroppedEventArgs(segment, sourceTrack, dropKind, dropTrack, dropRow, start));
                }

                InvalidateVisual();
            }
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);

            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        protected override void OnDrop(DragEventArgs e)
        {
            base.OnDrop(e);

            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            object? payload = e.Data.GetData(DataFormats.FileDrop);
            string[]? paths = payload as string[];
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            Point point = e.GetPosition(this);
            int rowIndex = RowAt(point);
            double seconds = SecondsAt(point);
            e.Handled = true;

            AudioFilesDropped?.Invoke(this, new AudioFilesDroppedEventArgs(paths, rowIndex, seconds));
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            Focus();

            Point point = e.GetPosition(this);
            int rowIndex = RowAt(point);
            if (tracks == null || rowIndex < 0)
            {
                return;
            }

            TrackContextMenuRequested?.Invoke(this, tracks[rowIndex]);
            e.Handled = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (snapLineSeconds.HasValue && !isSegmentDragging)
            {
                snapLineSeconds = null;
                InvalidateVisual();
            }
        }

        // ── Segment drag resolution ────────────────────────────────────────

        private void UpdateSegmentDrag(Point point)
        {
            if (dragSegment == null || tracks == null)
            {
                return;
            }

            double deltaSeconds = (point.X - segmentDragStartPoint.X) / PixelsPerSecond;
            double desired = dragSegment.StartSeconds + deltaSeconds;
            if (desired < 0.0)
            {
                desired = 0.0;
            }

            ResolveDropTarget(point, dragSegment);

            snapLineSeconds = null;

            if (segmentDropKind == TrackDropKind.ExistingTrack && segmentDropTrack != null)
            {
                double snapDistanceSeconds = SnapDistancePixels / PixelsPerSecond;
                double bestStart = desired;
                double bestDistance = double.MaxValue;
                bool snapped = false;

                List<TimelineSegment> neighbours = segmentDropTrack.SegmentsByStart();
                foreach (TimelineSegment neighbour in neighbours)
                {
                    if (ReferenceEquals(neighbour, dragSegment))
                    {
                        continue;
                    }

                    double beforeNeighbour = neighbour.StartSeconds - dragSegment.DurationSeconds;
                    double afterNeighbour = neighbour.EndSeconds;

                    if (Math.Abs(desired - beforeNeighbour) <= snapDistanceSeconds && Math.Abs(desired - beforeNeighbour) < bestDistance)
                    {
                        bestDistance = Math.Abs(desired - beforeNeighbour);
                        bestStart = Math.Max(0.0, beforeNeighbour);
                        snapLineSeconds = neighbour.StartSeconds;
                        snapped = true;
                    }

                    if (Math.Abs(desired - afterNeighbour) <= snapDistanceSeconds && Math.Abs(desired - afterNeighbour) < bestDistance)
                    {
                        bestDistance = Math.Abs(desired - afterNeighbour);
                        bestStart = afterNeighbour;
                        snapLineSeconds = neighbour.EndSeconds;
                        snapped = true;
                    }
                }

                if (!snapped && Math.Abs(desired) <= snapDistanceSeconds)
                {
                    bestStart = 0.0;
                    snapLineSeconds = 0.0;
                }

                segmentDragCandidateStart = bestStart;
                return;
            }

            segmentDragCandidateStart = desired;
        }

        private void ResolveDropTarget(Point point, TimelineSegment segment)
        {
            segmentDropKind = TrackDropKind.None;
            segmentDropTrack = null;
            segmentDropRowIndex = -1;

            if (tracks == null || tracks.Count == 0)
            {
                segmentDropKind = TrackDropKind.NewTrack;
                segmentDropRowIndex = 0;
                return;
            }

            int rowIndex = RowAt(point);
            if (rowIndex >= 0)
            {
                TimelineTrack candidate = tracks[rowIndex];
                if (candidate.Kind == segment.Kind)
                {
                    segmentDropKind = TrackDropKind.ExistingTrack;
                    segmentDropTrack = candidate;
                    segmentDropRowIndex = rowIndex;
                    return;
                }

                // Wrong kind: dropping between rows is still allowed.
                segmentDropKind = TrackDropKind.NewTrack;
                segmentDropRowIndex = point.Y < RowTop(rowIndex) + (RowHeight(candidate) / 2.0) ? rowIndex : rowIndex + 1;
                return;
            }

            segmentDropKind = TrackDropKind.NewTrack;
            segmentDropRowIndex = InsertIndexAt(point.Y);
        }

        // ── Change tracking ────────────────────────────────────────────────

        private void Attach()
        {
            if (tracks == null)
            {
                return;
            }

            tracks.CollectionChanged += OnTracksChanged;
            foreach (TimelineTrack track in tracks)
            {
                AttachTrack(track);
            }
        }

        private void Detach()
        {
            if (tracks == null)
            {
                return;
            }

            tracks.CollectionChanged -= OnTracksChanged;
            foreach (TimelineTrack track in tracks)
            {
                DetachTrack(track);
            }
        }

        private void AttachTrack(TimelineTrack track)
        {
            track.PropertyChanged += OnTrackChanged;
            track.Segments.CollectionChanged += OnSegmentsChanged;
            foreach (TimelineSegment segment in track.Segments)
            {
                segment.PropertyChanged += OnSegmentChanged;
            }
        }

        private void DetachTrack(TimelineTrack track)
        {
            track.PropertyChanged -= OnTrackChanged;
            track.Segments.CollectionChanged -= OnSegmentsChanged;
            foreach (TimelineSegment segment in track.Segments)
            {
                segment.PropertyChanged -= OnSegmentChanged;
            }
        }

        private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (object item in e.OldItems)
                {
                    TimelineTrack? track = item as TimelineTrack;
                    if (track != null)
                    {
                        DetachTrack(track);
                    }
                }
            }

            if (e.NewItems != null)
            {
                foreach (object item in e.NewItems)
                {
                    TimelineTrack? track = item as TimelineTrack;
                    if (track != null)
                    {
                        AttachTrack(track);
                    }
                }
            }

            InvalidateMeasure();
            InvalidateVisual();
        }

        private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (object item in e.OldItems)
                {
                    TimelineSegment? segment = item as TimelineSegment;
                    if (segment != null)
                    {
                        segment.PropertyChanged -= OnSegmentChanged;
                    }
                }
            }

            if (e.NewItems != null)
            {
                foreach (object item in e.NewItems)
                {
                    TimelineSegment? segment = item as TimelineSegment;
                    if (segment != null)
                    {
                        segment.PropertyChanged += OnSegmentChanged;
                    }
                }
            }

            InvalidateVisual();
        }

        private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
        {
            InvalidateVisual();
        }

        private void OnSegmentChanged(object? sender, PropertyChangedEventArgs e)
        {
            InvalidateVisual();
        }
    }
}
