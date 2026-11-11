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
    public class TrackMouseDownEventArgs : EventArgs
    {
        public TrackMouseDownEventArgs(TimelineTrack track, bool isHeader, bool controlPressed, double seconds, int clickCount)
        {
            Track = track;
            IsHeader = isHeader;
            ControlPressed = controlPressed;
            Seconds = seconds;
            ClickCount = clickCount;
        }

        public TimelineTrack Track { get; }

        public bool IsHeader { get; }

        public bool ControlPressed { get; }

        public double Seconds { get; }

        public int ClickCount { get; }
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
    /// Draws the multitrack timeline: one row per track with a header strip, a waveform
    /// (audio) or a thumbnail strip (video), the playhead, selection outlines, drag
    /// reordering and click-to-seek. Rendering is done in <see cref="OnRender"/> so a
    /// thousand peaks cost less than a thousand elements.
    /// </summary>
    public class TrackTimeline : FrameworkElement
    {
        public const double HeaderWidth = 152.0;
        public const double VideoRowHeight = 66.0;
        public const double AudioRowHeight = 58.0;
        public const double RowGap = 4.0;
        public const double EdgePadding = 2.0;

        private static readonly Typeface HeaderTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface BodyTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Color AccentColor = Color.FromRgb(0x2F, 0x81, 0xF7);
        private static readonly Color SelectionColor = Color.FromRgb(0xF2, 0xF6, 0xFC);

        private ObservableCollection<TimelineTrack>? tracks;
        private double totalDuration = 1.0;
        private double playheadSeconds;
        private int dragFromIndex = -1;
        private int dragInsertIndex = -1;
        private bool isDragging;
        private Point dragStartPoint;

        public TrackTimeline()
        {
            SnapsToDevicePixels = true;
            Focusable = false;
            ClipToBounds = true;
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

        public event EventHandler<TrackReorderEventArgs>? TrackReorderRequested;

        public event EventHandler<TimelineTrack>? TrackContextMenuRequested;

        public event EventHandler<double>? SeekRequested;

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
            double height = EdgePadding * 2.0;
            if (tracks == null)
            {
                return Math.Max(height + AudioRowHeight, 60.0);
            }

            for (int index = 0; index < tracks.Count; index++)
            {
                height += RowHeight(tracks[index]) + RowGap;
            }

            return Math.Max(height, 60.0);
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
            double top = EdgePadding;
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

        private int IndexAt(Point point)
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

        // ── Rendering ──────────────────────────────────────────────────────

        protected override void OnRender(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

            if (tracks == null || tracks.Count == 0)
            {
                DrawCenteredHint(drawingContext, "Open a video to build the timeline");
                return;
            }

            for (int index = 0; index < tracks.Count; index++)
            {
                DrawTrack(drawingContext, tracks[index], index);
            }

            if (isDragging && dragInsertIndex >= 0)
            {
                double y = dragInsertIndex >= tracks.Count
                    ? RowTop(tracks.Count - 1) + RowHeight(tracks[tracks.Count - 1])
                    : RowTop(dragInsertIndex) - (RowGap / 2.0);
                Pen pen = new Pen(new SolidColorBrush(AccentColor), 2.0);
                drawingContext.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
            }

            double playheadX = XForSeconds(playheadSeconds);
            Pen playheadPen = new Pen(new SolidColorBrush(AccentColor), 1.5);
            drawingContext.DrawLine(playheadPen, new Point(playheadX, 0), new Point(playheadX, ActualHeight));
        }

        private void DrawTrack(DrawingContext drawingContext, TimelineTrack track, int index)
        {
            double top = RowTop(index);
            double height = RowHeight(track);
            Rect rowRect = new Rect(0, top, ActualWidth, height);

            drawingContext.DrawRoundedRectangle(track.BackgroundBrush, null, rowRect, 4.0, 4.0);

            Rect headerRect = new Rect(4.0, top, HeaderWidth, height);
            DrawHeader(drawingContext, track, headerRect);

            Rect contentRect = new Rect(ContentLeft, top + 2.0, ContentWidth, height - 4.0);
            drawingContext.PushClip(new RectangleGeometry(contentRect));
            try
            {
                if (track.Kind == TrackKind.Video)
                {
                    DrawThumbnails(drawingContext, track, contentRect);
                }
                else
                {
                    DrawWaveform(drawingContext, track, contentRect);
                }
            }
            finally
            {
                drawingContext.Pop();
            }

            if (track.IsSelected)
            {
                Pen selectionPen = new Pen(new SolidColorBrush(SelectionColor), 2.0);
                drawingContext.DrawRoundedRectangle(null, selectionPen, new Rect(rowRect.X + 1.0, rowRect.Y + 1.0, rowRect.Width - 2.0, rowRect.Height - 2.0), 4.0, 4.0);
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

        private void DrawWaveform(DrawingContext drawingContext, TimelineTrack track, Rect contentRect)
        {
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double trackLeft = XForSeconds(track.StartSeconds);
            double trackRight = XForSeconds(track.EndSeconds);

            if (track.Samples.Length == 0)
            {
                FormattedText hint = new FormattedText(
                    "empty track — select it, then press Record",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    BodyTypeface,
                    11.0,
                    track.WaveformBrush,
                    pixelsPerDip);
                double hintX = Math.Max(trackLeft + 8.0, contentRect.X + 8.0);
                drawingContext.DrawText(hint, new Point(hintX, contentRect.Y + (contentRect.Height / 2.0) - 8.0));
                return;
            }

            double left = Math.Max(trackLeft, contentRect.X);
            double right = Math.Min(trackRight, contentRect.Right);
            int pixelCount = (int)Math.Round(right - left);
            if (pixelCount < 2 || track.DurationSeconds <= 0.0)
            {
                return;
            }

            double centerY = contentRect.Y + (contentRect.Height / 2.0);
            double halfHeight = Math.Max(2.0, (contentRect.Height / 2.0) - 3.0);
            double samplesPerPixel = Math.Max(1.0, (track.DurationSeconds * TimelineTrack.SampleRate) / pixelCount);

            double[] tops = new double[pixelCount];
            for (int pixel = 0; pixel < pixelCount; pixel++)
            {
                double timelineSeconds = track.StartSeconds + (track.DurationSeconds * ((pixel + 0.5) / pixelCount));
                double mediaSeconds = track.SourceOffsetSeconds + (timelineSeconds - track.StartSeconds);
                double peak = PeakFor(track, mediaSeconds, samplesPerPixel);
                double barHeight = Math.Max(0.6, peak * halfHeight);
                tops[pixel] = centerY - barHeight;
            }

            Brush waveformBrush = track.WaveformBrush;
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(new Point(left, centerY), true, true);
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    context.LineTo(new Point(left + pixel, tops[pixel]), false, false);
                }

                for (int pixel = pixelCount - 1; pixel >= 0; pixel--)
                {
                    double mirrored = centerY + (centerY - tops[pixel]);
                    context.LineTo(new Point(left + pixel, mirrored), false, false);
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(waveformBrush, null, geometry);
        }

        private static double PeakFor(TimelineTrack track, double mediaSeconds, double samplesPerPixel)
        {
            if (mediaSeconds < 0.0)
            {
                return 0.0;
            }

            int sampleIndex = (int)(mediaSeconds * TimelineTrack.SampleRate);
            if (sampleIndex >= track.Samples.Length || track.Peaks.Length == 0)
            {
                return 0.0;
            }

            int firstBucket = Math.Max(0, sampleIndex / TimelineTrack.PeakBucketSize);
            int lastBucket = Math.Min(track.Peaks.Length - 1, (int)((sampleIndex + samplesPerPixel) / TimelineTrack.PeakBucketSize));

            float peak = 0f;
            for (int bucket = firstBucket; bucket <= lastBucket; bucket++)
            {
                if (track.Peaks[bucket] > peak)
                {
                    peak = track.Peaks[bucket];
                }
            }

            return peak;
        }

        private void DrawThumbnails(DrawingContext drawingContext, TimelineTrack track, Rect contentRect)
        {
            if (track.Thumbnails.Length == 0 || track.DurationSeconds <= 0.0)
            {
                double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                FormattedText hint = new FormattedText(
                    "no preview frames",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    BodyTypeface,
                    11.0,
                    track.WaveformBrush,
                    pixelsPerDip);
                drawingContext.DrawText(hint, new Point(contentRect.X + 8.0, contentRect.Y + (contentRect.Height / 2.0) - 8.0));
                return;
            }

            double sourceDuration = track.EffectiveSourceDuration;
            if (sourceDuration <= 0.0)
            {
                sourceDuration = track.SourceOffsetSeconds + track.DurationSeconds;
            }

            if (sourceDuration <= 0.0)
            {
                return;
            }

            int count = track.Thumbnails.Length;
            for (int index = 0; index < count; index++)
            {
                double thumbnailStart = sourceDuration * index / count;
                double thumbnailEnd = sourceDuration * (index + 1) / count;

                double visibleStart = Math.Max(thumbnailStart, track.SourceOffsetSeconds);
                double visibleEnd = Math.Min(thumbnailEnd, track.SourceOffsetSeconds + track.DurationSeconds);
                if (visibleEnd <= visibleStart)
                {
                    continue;
                }

                double startFraction = (visibleStart - track.SourceOffsetSeconds) / track.DurationSeconds;
                double endFraction = (visibleEnd - track.SourceOffsetSeconds) / track.DurationSeconds;

                double trackLeft = XForSeconds(track.StartSeconds);
                double trackWidth = Math.Max(1.0, XForSeconds(track.EndSeconds) - trackLeft);
                double x = trackLeft + (trackWidth * startFraction);
                double width = trackWidth * (endFraction - startFraction);
                if (width <= 0.5)
                {
                    continue;
                }

                Rect target = new Rect(x, contentRect.Y, width, contentRect.Height);
                drawingContext.DrawImage(track.Thumbnails[index], target);

                Pen divider = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1.0);
                drawingContext.DrawLine(divider, new Point(x, contentRect.Y), new Point(x, contentRect.Y + contentRect.Height));
            }
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

        // ── Mouse ──────────────────────────────────────────────────────────

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            Point point = e.GetPosition(this);
            int index = IndexAt(point);
            if (tracks == null || index < 0)
            {
                SeekRequested?.Invoke(this, SecondsAt(point));
                return;
            }

            TimelineTrack track = tracks[index];
            bool isHeader = point.X < HeaderWidth;
            bool controlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            TrackMouseDown?.Invoke(this, new TrackMouseDownEventArgs(track, isHeader, controlPressed, SecondsAt(point), e.ClickCount));

            if (isHeader)
            {
                dragFromIndex = index;
                dragStartPoint = point;
                isDragging = false;
                CaptureMouse();
            }

            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (dragFromIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point point = e.GetPosition(this);
            if (!isDragging && Math.Abs(point.Y - dragStartPoint.Y) < 8.0)
            {
                return;
            }

            isDragging = true;
            dragInsertIndex = InsertIndexAt(point.Y);
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            if (dragFromIndex >= 0)
            {
                int fromIndex = dragFromIndex;
                int toIndex = dragInsertIndex;

                dragFromIndex = -1;
                dragInsertIndex = -1;

                if (isDragging)
                {
                    isDragging = false;
                    if (toIndex >= 0)
                    {
                        TrackReorderRequested?.Invoke(this, new TrackReorderEventArgs(fromIndex, toIndex));
                    }
                }

                if (IsMouseCaptured)
                {
                    ReleaseMouseCapture();
                }

                InvalidateVisual();
            }
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);

            Point point = e.GetPosition(this);
            int index = IndexAt(point);
            if (tracks == null || index < 0)
            {
                return;
            }

            TrackContextMenuRequested?.Invoke(this, tracks[index]);
            e.Handled = true;
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
                track.PropertyChanged += OnTrackChanged;
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
                track.PropertyChanged -= OnTrackChanged;
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
                        track.PropertyChanged -= OnTrackChanged;
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
                        track.PropertyChanged += OnTrackChanged;
                    }
                }
            }

            InvalidateMeasure();
            InvalidateVisual();
        }

        private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
        {
            InvalidateVisual();
        }
    }
}
