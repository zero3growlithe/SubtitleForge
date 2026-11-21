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

    /// <summary>
    /// One clip was right-clicked. The track travels with it because a clip is only meaningful inside
    /// the row it is drawn in: the host decides which lanes its sound can be split off from.
    /// </summary>
    public class TimelineSegmentContextEventArgs : EventArgs
    {
        public TimelineSegmentContextEventArgs(TimelineTrack track, TimelineSegment segment)
        {
            Track = track;
            Segment = segment;
        }

        public TimelineTrack Track { get; }

        public TimelineSegment Segment { get; }
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
        /// Supplies the cue marks painted in the ruler. They are the same opaque blue bars the seek
        /// strip under the video shows, so a cue can be found in either place.
        /// </summary>
        public Func<IReadOnlyList<CueMark>>? CueMarkProvider { get; set; }

        /// <summary>One cue as the ruler needs it: a span of time and whether it is selected.</summary>
        public sealed class CueMark
        {
            public CueMark(double startSeconds, double endSeconds, bool selected)
            {
                StartSeconds = startSeconds;
                EndSeconds = endSeconds;
                Selected = selected;
            }

            public double StartSeconds { get; }

            public double EndSeconds { get; }

            public bool Selected { get; }
        }

        /// <summary>
        /// One visible row of the timeline: a host track plus the slim lanes of the audio tracks
        /// attached to it. A row index is not a track index any more, because an attached track has
        /// no row of its own, which is why every row carries its <see cref="Track"/> instead.
        /// </summary>
        private sealed class TimelineRow
        {
            public TimelineRow(TimelineTrack track, double top, double height, double laneHeight, List<TimelineTrack> attachedTracks)
            {
                Track = track;
                Top = top;
                Height = height;
                LaneHeight = laneHeight;
                AttachedTracks = attachedTracks;
            }

            public TimelineTrack Track { get; }

            public double Top { get; }

            public double Height { get; }

            public double LaneHeight { get; }

            public List<TimelineTrack> AttachedTracks { get; }

            /// <summary>The part of the row the host's clips and the header text live in.</summary>
            public double HostHeight
            {
                get { return Height - (LaneHeight * AttachedTracks.Count); }
            }

            /// <summary>Top of the attached lane at <paramref name="laneIndex"/>, stacked at the bottom.</summary>
            public double LaneTop(int laneIndex)
            {
                return Top + HostHeight + (LaneHeight * laneIndex);
            }
        }

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

        /// <summary>
        /// Height of the slim lane an attached audio track is drawn in, before the vertical zoom.
        /// It has to read as a strip under the clips of its host, not as a row of its own.
        /// </summary>
        public const double AttachedLaneHeight = 24.0;

        /// <summary>Floor for the lane so the waveform still reads when the rows are squeezed.</summary>
        public const double MinimumAttachedLaneHeight = 14.0;

        private const double SegmentSidePadding = 0.5;
        private const double SegmentCornerRadius = 1.5;
        private const double SnapDistancePixels = 12.0;
        private const double DragThreshold = 4.0;

        private static readonly Typeface HeaderTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface BodyTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Color AccentColor = Color.FromRgb(0x2F, 0x81, 0xF7);
        private static readonly Color SelectionColor = Color.FromRgb(0xF2, 0xF6, 0xFC);
        /// <summary>The span a range transcription rewrites, drawn in the ruler.</summary>
        private static readonly Brush RangeBandBrush = Frozen(Color.FromArgb(0x60, 0x2F, 0x6F, 0xE0));

        /// <summary>
        /// The lane keeps the default audio green whatever colour its own track carries: the strip is
        /// part of the video row, so it borrows the audio palette instead of inventing a colour per
        /// import. The waveform and the divider are derived from that same green to stay readable.
        /// </summary>
        private static readonly Brush AttachedLaneBrush = CreateFrozenBrush(TrackColors.AudioDefault);
        private static readonly Brush AttachedLaneBackdropBrush = CreateFrozenBrush(Color.FromRgb(
            (byte)(TrackColors.AudioDefault.R * 0.5),
            (byte)(TrackColors.AudioDefault.G * 0.5),
            (byte)(TrackColors.AudioDefault.B * 0.5)));
        private static readonly Brush AttachedLaneWaveformBrush = CreateFrozenBrush(TrackColors.Contrasting(TrackColors.AudioDefault));
        private static readonly Brush AttachedLaneDividerBrush = CreateFrozenBrush(Color.FromRgb(0x1B, 0x1F, 0x1C));

        private double stickyOffset;
        private double? rangeStartSeconds;
        private double? rangeEndSeconds;

        private double horizontalZoom = 1.0;
        private double verticalZoom = 1.0;
        private double viewStartSeconds;

        /// <summary>The whole timeline is this many seconds wide at zoom 1; more means fewer seconds.</summary>
        public const double MaximumHorizontalZoom = 60.0;
        public const double MinimumVerticalZoom = 0.45;
        public const double MaximumVerticalZoom = 2.4;
        private const double ZoomStep = 1.18;

        /// <summary>How many pixels wide one preview frame is drawn, at any zoom level.</summary>
        private const double ThumbnailSpacingPixels = 72.0;

        /// <summary>Raised when zooming or scrolling changed what part of the timeline is on screen.</summary>
        public event EventHandler? ViewChanged;

        private static readonly Color RulerColor = Color.FromRgb(0x2A, 0x2A, 0x31);
        private static readonly Brush ReadyBandBrush = CreateFrozenBrush(AccentColor);
        private static readonly Brush CueMarkBrush = CreateFrozenBrush(Color.FromRgb(0x27, 0x56, 0x8F));
        /// <summary>
        /// The second cue shade. Cues that touch leave no gap between their bars, so a single
        /// colour made a row of them read as one long bar; alternating two blues keeps every cue
        /// countable without moving any of the marks.
        /// </summary>
        private static readonly Brush CueMarkAlternateBrush = CreateFrozenBrush(Color.FromRgb(0x1B, 0x3F, 0x6B));
        private static readonly Color RulerTextColor = Color.FromRgb(0x9A, 0xA0, 0xAC);

        private ObservableCollection<TimelineTrack>? tracks;
        private double totalDuration = 1.0;
        private double playheadSeconds;

        private bool isScrubbing;

        private int rowDragFromIndex = -1;
        private int rowDragInsertIndex = -1;
        private double rowDragInsertLineY;
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
                    ClampViewStart();
                    InvalidateVisual();
                    RaiseViewChanged();
                }
            }
        }

        /// <summary>
        /// How far the timeline is stretched out horizontally. 1 shows the whole duration, 4 shows a
        /// quarter of it, and a horizontal scrollbar appears as soon as it is above 1.
        /// </summary>
        public double HorizontalZoom
        {
            get { return horizontalZoom; }
            set
            {
                double updated = value;
                if (updated < 1.0)
                {
                    updated = 1.0;
                }

                if (updated > MaximumHorizontalZoom)
                {
                    updated = MaximumHorizontalZoom;
                }

                if (Math.Abs(horizontalZoom - updated) > 0.0001)
                {
                    horizontalZoom = updated;
                    ClampViewStart();
                    InvalidateVisual();
                    RaiseViewChanged();
                }
            }
        }

        /// <summary>
        /// Row height multiplier. Below 1 the tracks get shorter so more of them fit on screen;
        /// above 1 the waveforms and thumbnails get more room.
        /// </summary>
        public double VerticalZoom
        {
            get { return verticalZoom; }
            set
            {
                double updated = value;
                if (updated < MinimumVerticalZoom)
                {
                    updated = MinimumVerticalZoom;
                }

                if (updated > MaximumVerticalZoom)
                {
                    updated = MaximumVerticalZoom;
                }

                if (Math.Abs(verticalZoom - updated) > 0.0001)
                {
                    verticalZoom = updated;
                    InvalidateMeasure();
                    InvalidateVisual();
                    RaiseViewChanged();
                }
            }
        }

        /// <summary>The left edge of the visible part of the timeline, in seconds.</summary>
        public double ViewStartSeconds
        {
            get { return viewStartSeconds; }
            set
            {
                double updated = value;
                double maximum = MaximumViewStart();
                if (updated > maximum)
                {
                    updated = maximum;
                }

                if (updated < 0.0)
                {
                    updated = 0.0;
                }

                if (Math.Abs(viewStartSeconds - updated) > 0.0001)
                {
                    viewStartSeconds = updated;
                    InvalidateVisual();
                    // A programmatic scroll (the scrollbar, following the playhead) has to reach the
                    // scrollbar synchronisation and the thumbnail debounce exactly like a wheel does.
                    RaiseViewChanged();
                }
            }
        }

        /// <summary>How many seconds fit in the timeline area right now.</summary>
        public double VisibleSeconds
        {
            get
            {
                double visible = totalDuration / Math.Max(1.0, horizontalZoom);
                return visible <= 0.05 ? 0.05 : visible;
            }
        }

        /// <summary>
        /// Seconds of the timeline one preview frame stands for at the current zoom: the visible
        /// span divided by how many 72 px frames fit across the track area. Zooming in shrinks it,
        /// which is what makes the owner request more frames instead of stretching the few it has.
        /// </summary>
        public double ThumbnailSpacingSeconds
        {
            get
            {
                // Before the first layout the width is zero; assuming a normal panel keeps the
                // first strip from being asked for at a nonsense density.
                double contentWidth = ActualWidth > 240.0 ? ContentWidth : 900.0;
                double framesAcross = Math.Floor(contentWidth / ThumbnailSpacingPixels);
                if (framesAcross < 1.0)
                {
                    framesAcross = 1.0;
                }

                return VisibleSeconds / framesAcross;
            }
        }

        public double MaximumViewStart()
        {
            double maximum = totalDuration - VisibleSeconds;
            return maximum < 0.0 ? 0.0 : maximum;
        }

        /// <summary>The horizontal zoom and scroll, the way the scrollbar needs them.</summary>
        public void SetVisibleRange(double startSeconds, double spanSeconds)
        {
            if (spanSeconds <= 0.0)
            {
                return;
            }

            double zoom = totalDuration / spanSeconds;
            if (zoom < 1.0)
            {
                zoom = 1.0;
            }

            if (zoom > MaximumHorizontalZoom)
            {
                zoom = MaximumHorizontalZoom;
            }

            horizontalZoom = zoom;
            viewStartSeconds = startSeconds;
            ClampViewStart();
            InvalidateVisual();
            RaiseViewChanged();
        }

        private void ClampViewStart()
        {
            double maximum = MaximumViewStart();
            if (viewStartSeconds > maximum)
            {
                viewStartSeconds = maximum;
            }

            if (viewStartSeconds < 0.0)
            {
                viewStartSeconds = 0.0;
            }
        }

        private void RaiseViewChanged()
        {
            EventHandler? handler = ViewChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
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

        /// <summary>
        /// Raised for a right-click on a clip. Empty lane space and the header raise
        /// <see cref="TrackContextMenuRequested"/> instead, so the two menus can never both answer
        /// the same click: the clip menu speaks for what was clicked, the track menu for the row.
        /// </summary>
        public event EventHandler<TimelineSegmentContextEventArgs>? SegmentContextMenuRequested;

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

        /// <summary>
        /// How far the surrounding scroll viewer has scrolled. The ruler is painted a second time at
        /// that offset, so it stays on screen however many tracks push the rows down.
        /// </summary>
        public double StickyOffset
        {
            get { return stickyOffset; }
            set
            {
                double updated = value < 0.0 ? 0.0 : value;
                if (Math.Abs(stickyOffset - updated) > 0.5)
                {
                    stickyOffset = updated;
                    InvalidateVisual();
                }
            }
        }

        /// <summary>Marks the span a range transcription will rewrite. Both null clears it.</summary>
        public void SetRange(double? startSeconds, double? endSeconds)
        {
            double? start = startSeconds;
            double? end = endSeconds;
            if (start.HasValue && end.HasValue && end.Value < start.Value)
            {
                double swap = start.Value;
                start = end.Value;
                end = swap;
            }

            bool same = rangeStartSeconds == start && rangeEndSeconds == end;
            if (same)
            {
                return;
            }

            rangeStartSeconds = start;
            rangeEndSeconds = end;
            InvalidateVisual();
        }

        private static Brush Frozen(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
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

            foreach (TimelineRow row in BuildRows())
            {
                height += row.Height + RowGap;
            }

            return Math.Max(height, RulerHeight + AudioRowHeight);
        }

        /// <summary>Height of a track on its own, before any attached lane is folded in.</summary>
        private double BaseRowHeight(TimelineTrack track)
        {
            double baseHeight = track.Kind == TrackKind.Video ? VideoRowHeight : AudioRowHeight;
            double scaled = baseHeight * verticalZoom;
            // Enough room for the header text to stay readable at any scale.
            return scaled < 26.0 ? 26.0 : scaled;
        }

        private double ScaledAttachedLaneHeight
        {
            get
            {
                double scaled = AttachedLaneHeight * verticalZoom;
                return scaled < MinimumAttachedLaneHeight ? MinimumAttachedLaneHeight : scaled;
            }
        }

        /// <summary>
        /// Geometry of every visible row, top to bottom. An audio track attached to a video track is
        /// folded into the host's row as a slim lane, so it produces no row of its own and the host
        /// grows by one lane per attachment.
        /// </summary>
        private List<TimelineRow> BuildRows()
        {
            List<TimelineRow> rows = new List<TimelineRow>();
            if (tracks == null)
            {
                return rows;
            }

            // The lanes are collected first: a host may carry more than one, and its height is only
            // known once its lane count is.
            Dictionary<TimelineTrack, List<TimelineTrack>> lanesByHost =
                new Dictionary<TimelineTrack, List<TimelineTrack>>();
            foreach (TimelineTrack track in tracks)
            {
                TimelineTrack? host = AttachedHost(track);
                if (host == null)
                {
                    continue;
                }

                List<TimelineTrack> lanes;
                if (!lanesByHost.TryGetValue(host, out lanes))
                {
                    lanes = new List<TimelineTrack>();
                    lanesByHost[host] = lanes;
                }

                lanes.Add(track);
            }

            double laneHeight = ScaledAttachedLaneHeight;
            double top = RulerHeight + EdgePadding;

            foreach (TimelineTrack track in tracks)
            {
                if (AttachedHost(track) != null)
                {
                    // It lives inside its host's row.
                    continue;
                }

                List<TimelineTrack> lanes;
                if (!lanesByHost.TryGetValue(track, out lanes))
                {
                    lanes = new List<TimelineTrack>();
                }

                double height = BaseRowHeight(track) + (laneHeight * lanes.Count);
                rows.Add(new TimelineRow(track, top, height, laneHeight, lanes));
                top += height + RowGap;
            }

            return rows;
        }

        /// <summary>
        /// The host a track rides in, or null when it stands alone. The link only counts while the
        /// host is still on the timeline, is a video track and is not the track itself: a stale link
        /// must never hide a lane.
        /// </summary>
        private TimelineTrack? AttachedHost(TimelineTrack track)
        {
            TimelineTrack? host = track.AttachedHostTrack;
            if (host == null || tracks == null || ReferenceEquals(host, track))
            {
                return null;
            }

            if (track.Kind != TrackKind.Audio || host.Kind != TrackKind.Video)
            {
                return null;
            }

            return tracks.Contains(host) ? host : null;
        }

        private static TimelineRow? RowFor(List<TimelineRow> rows, TimelineTrack track)
        {
            foreach (TimelineRow row in rows)
            {
                if (ReferenceEquals(row.Track, track))
                {
                    return row;
                }
            }

            return null;
        }

        private double ContentLeft
        {
            get { return HeaderWidth + 4.0; }
        }

        private double ContentWidth
        {
            get { return Math.Max(1.0, ActualWidth - ContentLeft - 4.0); }
        }

        /// <summary>The visible row under the point, or null above and below the rows.</summary>
        private TimelineRow? RowAt(Point point)
        {
            return RowAt(BuildRows(), point);
        }

        private static TimelineRow? RowAt(List<TimelineRow> rows, Point point)
        {
            foreach (TimelineRow row in rows)
            {
                if (point.Y >= row.Top && point.Y <= row.Top + row.Height)
                {
                    return row;
                }
            }

            return null;
        }

        /// <summary>
        /// Where a new track would be inserted for a drop at <paramref name="y"/>, as an index into
        /// <see cref="tracks"/> — rows and tracks are different lists now that an attached track has
        /// no row of its own. <paramref name="lineY"/> is where the insertion line is drawn.
        /// </summary>
        private int InsertIndexAt(double y, out double lineY)
        {
            List<TimelineRow> rows = BuildRows();
            lineY = RulerHeight + EdgePadding;

            if (tracks == null || tracks.Count == 0)
            {
                return 0;
            }

            foreach (TimelineRow row in rows)
            {
                double middle = row.Top + (row.Height / 2.0);
                if (y < middle)
                {
                    lineY = row.Top - (RowGap / 2.0);
                    int index = tracks.IndexOf(row.Track);
                    return index < 0 ? 0 : index;
                }

                lineY = row.Top + row.Height + (RowGap / 2.0);
            }

            return tracks.Count;
        }

        private double SecondsAt(Point point)
        {
            double seconds = viewStartSeconds + ((point.X - ContentLeft) / PixelsPerSecond);
            if (seconds < 0.0)
            {
                seconds = 0.0;
            }

            if (seconds > totalDuration)
            {
                seconds = totalDuration;
            }

            return seconds;
        }

        private double XForSeconds(double seconds)
        {
            return ContentLeft + ((seconds - viewStartSeconds) * PixelsPerSecond);
        }

        private double PixelsPerSecond
        {
            get { return ContentWidth / VisibleSeconds; }
        }

        /// <summary>True when the time is inside the part of the timeline that is on screen.</summary>
        private bool IsTimeVisible(double seconds)
        {
            double viewEnd = viewStartSeconds + VisibleSeconds;
            return seconds > viewStartSeconds - 0.001 && seconds < viewEnd + 0.001;
        }

        // ── Zoom ───────────────────────────────────────────────────────────

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);

            // The window routes the wheel by where the cursor is, so this only runs when nothing
            // above the control claimed the notch.
            if (HandleMouseWheel(e.Delta, Keyboard.Modifiers))
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// Applies one wheel notch at the cursor: Alt zooms around the pointer, Ctrl changes the row
        /// height, Shift scrolls along the timeline. Returns true when the notch was used, so the
        /// caller can keep the enclosing scroll viewer from scrolling as well.
        /// </summary>
        public bool HandleMouseWheel(int delta, ModifierKeys modifiers)
        {
            int steps = delta / 120;
            if (steps == 0)
            {
                steps = delta > 0 ? 1 : -1;
            }

            double factor = steps > 0 ? ZoomStep : 1.0 / ZoomStep;
            bool alt = (modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            bool control = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (alt && !control)
            {
                // Zoom around the pointer: whatever second is under the cursor stays under it.
                double anchorX = Mouse.GetPosition(this).X;
                double anchorSeconds = SecondsAt(new Point(anchorX, 0.0));
                double offsetFraction = ContentWidth <= 0.0 ? 0.0 : (anchorX - ContentLeft) / ContentWidth;

                HorizontalZoom *= factor;
                viewStartSeconds = anchorSeconds - (offsetFraction * VisibleSeconds);
                ClampViewStart();
                InvalidateVisual();
                RaiseViewChanged();
                return true;
            }

            if (control && !alt)
            {
                VerticalZoom *= factor;
                return true;
            }

            if (shift && !control && !alt && horizontalZoom > 1.0001)
            {
                // Shift scrolls along the timeline instead of moving through the row list.
                ViewStartSeconds += -steps * VisibleSeconds * 0.12;
                InvalidateVisual();
                RaiseViewChanged();
                return true;
            }

            return false;
        }

        /// <summary>Puts the middle of the visible span on the playhead. Used while playing when zoomed.</summary>
        public void FollowPlayhead()
        {
            if (horizontalZoom <= 1.0001)
            {
                return;
            }

            double viewEnd = viewStartSeconds + VisibleSeconds;
            if (playheadSeconds >= viewStartSeconds && playheadSeconds <= viewEnd)
            {
                return;
            }

            ViewStartSeconds = playheadSeconds - (VisibleSeconds / 2.0);
        }

        // ── Rendering ──────────────────────────────────────────────────────

        protected override void OnRender(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            DrawRuler(drawingContext, 0.0);

            if (tracks == null || tracks.Count == 0)
            {
                DrawCenteredHint(drawingContext, "Open a video to build the timeline");
                return;
            }

            List<TimelineRow> rows = BuildRows();
            for (int index = 0; index < rows.Count; index++)
            {
                DrawRow(drawingContext, rows[index]);
            }

            if (isRowDragging && rowDragInsertIndex >= 0)
            {
                Pen pen = new Pen(new SolidColorBrush(AccentColor), 2.0);
                drawingContext.DrawLine(pen, new Point(0, rowDragInsertLineY), new Point(ActualWidth, rowDragInsertLineY));
            }

            if (isSegmentDragging)
            {
                DrawSegmentDragPreview(drawingContext);
            }

            if (snapLineSeconds.HasValue && IsTimeVisible(snapLineSeconds.Value))
            {
                double snapX = XForSeconds(snapLineSeconds.Value);
                Pen snapPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66)), 1.5);
                drawingContext.DrawLine(snapPen, new Point(snapX, RulerHeight), new Point(snapX, ActualHeight));
            }

            Pen playheadPen = new Pen(new SolidColorBrush(AccentColor), 1.5);
            double playheadX = XForSeconds(playheadSeconds);
            if (IsTimeVisible(playheadSeconds))
            {
                drawingContext.DrawLine(playheadPen, new Point(playheadX, 0), new Point(playheadX, ActualHeight));
            }

            if (stickyOffset <= 0.5)
            {
                return;
            }

            // The rows above have scrolled past the top of the panel, so the ruler is painted a
            // second time at the scroll offset. It covers the rows scrolling behind it and stays
            // put no matter how far down the list the user goes.
            DrawRuler(drawingContext, stickyOffset);
            drawingContext.DrawLine(
                new Pen(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1E)), 1.0),
                new Point(0, stickyOffset + RulerHeight),
                new Point(ActualWidth, stickyOffset + RulerHeight));

            // The playhead used to be hidden behind the band; it is drawn over it so the position
            // stays readable while scrolled.
            if (IsTimeVisible(playheadSeconds))
            {
                drawingContext.DrawLine(
                    playheadPen,
                    new Point(playheadX, stickyOffset),
                    new Point(playheadX, ActualHeight));
            }
        }

        private void DrawRuler(DrawingContext drawingContext, double top)
        {
            drawingContext.DrawRectangle(new SolidColorBrush(RulerColor), null, new Rect(0, top, ActualWidth, RulerHeight));

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Brush textBrush = new SolidColorBrush(RulerTextColor);
            Pen tickPen = new Pen(new SolidColorBrush(Color.FromRgb(0x53, 0x53, 0x5F)), 1.0);

            double step = ChooseTickStep();
            if (step <= 0.0)
            {
                return;
            }

            // Ticks and labels may not run into the track headers once the timeline is zoomed.
            drawingContext.PushClip(new RectangleGeometry(new Rect(ContentLeft, top, Math.Max(0.0, ContentWidth), RulerHeight)));
            try
            {
                double viewEnd = viewStartSeconds + VisibleSeconds;
                double seconds = Math.Floor(viewStartSeconds / step) * step;
                while (seconds <= viewEnd + 0.0001)
                {
                    if (seconds < -0.0001)
                    {
                        seconds += step;
                        continue;
                    }

                    double x = XForSeconds(seconds);
                    bool major = Math.Abs(seconds % (step * 5.0)) < 0.001 || Math.Abs(seconds) < 0.001;
                    double tickHeight = major ? RulerHeight - 8.0 : RulerHeight - 4.0;

                    drawingContext.DrawLine(tickPen, new Point(x, top + tickHeight), new Point(x, top + RulerHeight));

                    if (major)
                    {
                        FormattedText label = new FormattedText(
                            FormatClock(seconds, step < 1.0),
                            CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            BodyTypeface,
                            9.5,
                            textBrush,
                            pixelsPerDip);
                        drawingContext.DrawText(label, new Point(x + 3.0, top + 2.0));
                    }

                    seconds += step;
                }
            }
            finally
            {
                drawingContext.Pop();
            }

            drawingContext.PushClip(new RectangleGeometry(new Rect(ContentLeft, top, Math.Max(0.0, ContentWidth), RulerHeight)));
            try
            {
                DrawRangeBand(drawingContext, top);
                DrawCueMarks(drawingContext, top);
            }
            finally
            {
                drawingContext.Pop();
            }
        }

        /// <summary>
        /// The span a range transcription will rewrite. It sits under the cue marks so the marks
        /// still read, and its edges are marked so the user can see exactly what will be replaced.
        /// </summary>
        private void DrawRangeBand(DrawingContext drawingContext, double top)
        {
            if (!rangeStartSeconds.HasValue || !rangeEndSeconds.HasValue)
            {
                return;
            }

            double start = Math.Min(rangeStartSeconds.Value, rangeEndSeconds.Value);
            double end = Math.Max(rangeStartSeconds.Value, rangeEndSeconds.Value);
            if (end - start <= 0.0001)
            {
                return;
            }

            double left = Math.Max(XForSeconds(start), HeaderWidth);
            double right = Math.Min(XForSeconds(end), ActualWidth);
            if (right <= left)
            {
                return;
            }

            drawingContext.DrawRectangle(RangeBandBrush, null, new Rect(left, top, right - left, RulerHeight));
            Pen edgePen = new Pen(new SolidColorBrush(AccentColor), 1.0);
            drawingContext.DrawLine(edgePen, new Point(left, top), new Point(left, top + RulerHeight));
            drawingContext.DrawLine(edgePen, new Point(right, top), new Point(right, top + RulerHeight));
        }

        private void DrawCueMarks(DrawingContext drawingContext, double top)
        {
            Func<IReadOnlyList<CueMark>>? provider = CueMarkProvider;
            if (provider == null)
            {
                return;
            }

            IReadOnlyList<CueMark> marks;
            try
            {
                marks = provider();
            }
            catch (Exception)
            {
                return;
            }

            if (marks == null || marks.Count == 0)
            {
                return;
            }

            double markHeight = 5.0;
            double markTop = top + RulerHeight - markHeight - 1.0;

            for (int index = 0; index < marks.Count; index++)
            {
                CueMark mark = marks[index];
                double left = XForSeconds(mark.StartSeconds);
                double right = XForSeconds(mark.EndSeconds);
                if (right - left < 2.0)
                {
                    right = left + 2.0;
                }

                double contentLeft = HeaderWidth;
                double clipLeft = Math.Max(left, contentLeft);
                double clipRight = Math.Min(right, ActualWidth);
                if (clipRight <= clipLeft)
                {
                    continue;
                }

                // Fully opaque, same as the seek strip: the bars have to read clearly against the
                // ruler. A selected cue keeps the bright ready colour; the others alternate by cue
                // order, so two neighbours that touch never merge into one bar. The parity follows
                // the cue, not the neighbours, which keeps the shades still while selecting.
                Brush brush;
                if (mark.Selected)
                {
                    brush = ReadyBandBrush;
                }
                else if (index % 2 == 0)
                {
                    brush = CueMarkBrush;
                }
                else
                {
                    brush = CueMarkAlternateBrush;
                }

                drawingContext.DrawRectangle(
                    brush,
                    null,
                    new Rect(clipLeft, markTop, clipRight - clipLeft, markHeight));
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

        /// <summary>
        /// m:ss, or m:ss.t once the zoom makes half-second ticks far enough apart to label them —
        /// without the tenth the same label would repeat twice while placing a clip edge.
        /// </summary>
        private static string FormatClock(double seconds, bool withTenths)
        {
            if (seconds < 0.0)
            {
                seconds = 0.0;
            }

            if (!withTenths)
            {
                int totalSeconds = (int)Math.Round(seconds);
                return (totalSeconds / 60).ToString(CultureInfo.InvariantCulture)
                    + ":"
                    + (totalSeconds % 60).ToString("00", CultureInfo.InvariantCulture);
            }

            int wholeSeconds = (int)Math.Floor(seconds);
            int tenths = (int)Math.Round((seconds - wholeSeconds) * 10.0);
            if (tenths >= 10)
            {
                tenths = 9;
            }

            return (wholeSeconds / 60).ToString(CultureInfo.InvariantCulture)
                + ":"
                + (wholeSeconds % 60).ToString("00", CultureInfo.InvariantCulture)
                + "."
                + tenths.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// One visible row: the host's own lane and clips, followed by the slim lanes of the audio
        /// tracks attached to it. The picture keeps the full host height and the sound sits in the
        /// strip underneath it, so a video track with its audio reads as a single row.
        /// </summary>
        private void DrawRow(DrawingContext drawingContext, TimelineRow row)
        {
            TimelineTrack track = row.Track;
            double top = row.Top;
            double height = row.Height;
            double hostHeight = row.HostHeight;

            drawingContext.DrawRectangle(track.LaneBrush, null, new Rect(0, top, ActualWidth, height));

            // The header only covers the host part: its text has no business sitting on the lane.
            Rect headerRect = new Rect(4.0, top, HeaderWidth, hostHeight);
            DrawHeader(drawingContext, track, headerRect);

            // Zooming can push a segment past either edge of the panel; the clip keeps it off the
            // track headers and off whatever sits next to the timeline.
            drawingContext.PushClip(new RectangleGeometry(new Rect(ContentLeft, top, Math.Max(0.0, ContentWidth), hostHeight)));
            try
            {
                foreach (TimelineSegment segment in track.Segments)
                {
                    DrawSegment(drawingContext, track, segment, top, hostHeight);
                }
            }
            finally
            {
                drawingContext.Pop();
            }

            for (int laneIndex = 0; laneIndex < row.AttachedTracks.Count; laneIndex++)
            {
                DrawAttachedLane(drawingContext, row, laneIndex);
            }

            // Drawn last so the outline runs around the lane as well: the whole row is one selection.
            if (track.IsSelected)
            {
                Pen selectionPen = new Pen(new SolidColorBrush(SelectionColor), 2.0);
                drawingContext.DrawRoundedRectangle(null, selectionPen, new Rect(1.0, top + 1.0, ActualWidth - 2.0, height - 2.0), 4.0, 4.0);
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
                drawingContext.DrawText(hint, new Point(hintX, top + (hostHeight / 2.0) - 8.0));
            }
        }

        /// <summary>
        /// The strip an attached audio track is drawn in: waveform only, on the default audio green,
        /// with no name and no colour swatch of its own — the row it is folded into owns those. Its
        /// clips are drawn but never header-labelled, so the lane stays slimmer than a real row.
        /// </summary>
        private void DrawAttachedLane(DrawingContext drawingContext, TimelineRow row, int laneIndex)
        {
            TimelineTrack attachedTrack = row.AttachedTracks[laneIndex];
            double laneTop = row.LaneTop(laneIndex);
            double laneHeight = row.LaneHeight;
            Rect laneArea = new Rect(ContentLeft, laneTop, Math.Max(0.0, ContentWidth), laneHeight);

            drawingContext.DrawRectangle(AttachedLaneBackdropBrush, null, new Rect(0, laneTop, ActualWidth, laneHeight));

            drawingContext.PushClip(new RectangleGeometry(laneArea));
            try
            {
                foreach (TimelineSegment segment in attachedTrack.Segments)
                {
                    Rect rect = SegmentRect(segment, laneTop, laneHeight);
                    if (rect.Width <= 0.5)
                    {
                        continue;
                    }

                    Rect onScreen = Rect.Intersect(rect, laneArea);
                    if (onScreen.IsEmpty || onScreen.Width <= 0.5)
                    {
                        continue;
                    }

                    drawingContext.DrawRoundedRectangle(AttachedLaneBrush, null, rect, SegmentCornerRadius, SegmentCornerRadius);

                    drawingContext.PushClip(new RectangleGeometry(onScreen));
                    try
                    {
                        DrawWaveform(drawingContext, AttachedLaneWaveformBrush, segment, rect, onScreen);
                    }
                    finally
                    {
                        drawingContext.Pop();
                    }
                }
            }
            finally
            {
                drawingContext.Pop();
            }

            // A hairline keeps the lane from reading as the bottom of the clips above it.
            drawingContext.DrawLine(
                new Pen(AttachedLaneDividerBrush, 1.0),
                new Point(0, laneTop),
                new Point(ActualWidth, laneTop));
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

            // A clip can be minutes long, and zoomed in it can be tens of thousands of pixels wide.
            // Only the part inside the timeline area is worth drawing: it is the same picture, it
            // keeps the waveform arrays small, and it keeps a long clip off the track headers.
            Rect onScreen = Rect.Intersect(rect, new Rect(ContentLeft, rowTop, Math.Max(0.0, ContentWidth), rowHeight));
            if (onScreen.IsEmpty || onScreen.Width <= 0.5)
            {
                return;
            }

            drawingContext.DrawRoundedRectangle(track.BackgroundBrush, null, rect, SegmentCornerRadius, SegmentCornerRadius);

            drawingContext.PushClip(new RectangleGeometry(onScreen));
            try
            {
                if (segment.Kind == TrackKind.Video)
                {
                    DrawThumbnails(drawingContext, track, segment, rect, onScreen);
                }
                else
                {
                    DrawWaveform(drawingContext, track.WaveformBrush, segment, rect, onScreen);
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

        private void DrawWaveform(DrawingContext drawingContext, Brush waveformBrush, TimelineSegment segment, Rect fullRect, Rect rect)
        {
            if (segment.Samples.Length == 0)
            {
                return;
            }

            int pixelCount = (int)Math.Round(rect.Width);
            if (pixelCount < 2 || segment.DurationSeconds <= 0.0 || fullRect.Width <= 0.0)
            {
                return;
            }

            // rect is the slice of the clip that is on screen, so the samples have to be walked over
            // that slice only — otherwise a zoomed-in clip would squeeze minutes into a few hundred
            // pixels and the waveform would stop matching the sound.
            double visibleFraction = Math.Min(1.0, rect.Width / fullRect.Width);
            if (visibleFraction <= 0.0)
            {
                return;
            }

            double leftFraction = (rect.X - fullRect.X) / fullRect.Width;

            double centerY = rect.Y + (rect.Height / 2.0);
            double halfHeight = Math.Max(2.0, (rect.Height / 2.0) - 3.0);
            double samplesPerPixel = Math.Max(
                1.0,
                (segment.DurationSeconds * visibleFraction * TimelineSegment.SampleRate) / pixelCount);

            double[] tops = new double[pixelCount];
            for (int pixel = 0; pixel < pixelCount; pixel++)
            {
                double localFraction = leftFraction + (visibleFraction * ((pixel + 0.5) / pixelCount));
                double timelineSeconds = segment.StartSeconds + (segment.DurationSeconds * localFraction);
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
            drawingContext.DrawGeometry(waveformBrush, null, geometry);
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

        private void DrawThumbnails(DrawingContext drawingContext, TimelineTrack track, TimelineSegment segment, Rect fullRect, Rect rect)
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

            // The strip carries the range it was sampled from: a zoomed-in strip covers a slice of
            // the file, and both halves of a split share one strip over the clip they came from.
            double stripStart = segment.ThumbnailSourceStartSeconds;
            if (stripStart < 0.0)
            {
                stripStart = 0.0;
            }

            double stripSpan = segment.ThumbnailSourceSpanSeconds > 0.0005
                ? segment.ThumbnailSourceSpanSeconds
                : sourceDuration;

            double visibleFraction = fullRect.Width <= 0.0 ? 1.0 : Math.Min(1.0, rect.Width / fullRect.Width);
            if (visibleFraction <= 0.0)
            {
                return;
            }

            double leftFraction = fullRect.Width <= 0.0 ? 0.0 : (rect.X - fullRect.X) / fullRect.Width;
            double rightFraction = leftFraction + visibleFraction;

            int count = segment.Thumbnails.Length;
            for (int index = 0; index < count; index++)
            {
                double thumbnailStart = stripStart + (stripSpan * index / count);
                double thumbnailEnd = stripStart + (stripSpan * (index + 1) / count);

                double visibleStart = Math.Max(thumbnailStart, segment.SourceOffsetSeconds);
                double visibleEnd = Math.Min(thumbnailEnd, segment.SourceOffsetSeconds + segment.DurationSeconds);
                if (visibleEnd <= visibleStart)
                {
                    continue;
                }

                double startFraction = (visibleStart - segment.SourceOffsetSeconds) / segment.DurationSeconds;
                double endFraction = (visibleEnd - segment.SourceOffsetSeconds) / segment.DurationSeconds;

                // Keep only the part of the thumbnail that is on screen and place it there.
                double onScreenStart = Math.Max(startFraction, leftFraction);
                double onScreenEnd = Math.Min(endFraction, rightFraction);
                if (onScreenEnd <= onScreenStart)
                {
                    continue;
                }

                double x = rect.X + (rect.Width * ((onScreenStart - leftFraction) / visibleFraction));
                double width = rect.Width * ((onScreenEnd - onScreenStart) / visibleFraction);
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

            List<TimelineRow> rows = BuildRows();

            double rowTop;
            double rowHeight;
            if (segmentDropKind == TrackDropKind.ExistingTrack && segmentDropTrack != null)
            {
                TimelineRow? target = RowFor(rows, segmentDropTrack);
                if (target == null)
                {
                    return;
                }

                rowTop = target.Top;
                rowHeight = target.HostHeight;
            }
            else if (segmentDropKind == TrackDropKind.NewTrack)
            {
                double insertionY = NewTrackInsertionY(rows);
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

        /// <summary>
        /// Where a brand-new track would appear for the current drop: in front of the track at the
        /// reported collection index, or under the last row when the drop is past the end.
        /// </summary>
        private double NewTrackInsertionY(List<TimelineRow> rows)
        {
            if (tracks != null && segmentDropRowIndex >= 0 && segmentDropRowIndex < tracks.Count)
            {
                TimelineRow? target = RowFor(rows, tracks[segmentDropRowIndex]);
                if (target != null)
                {
                    return target.Top;
                }
            }

            if (rows.Count == 0)
            {
                return RulerHeight + EdgePadding;
            }

            TimelineRow last = rows[rows.Count - 1];
            return last.Top + last.Height + RowGap;
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

        /// <summary>True inside the ruler, wherever its sticky copy currently sits.</summary>
        private bool IsRuler(double y)
        {
            return y >= stickyOffset - 0.5 && y <= stickyOffset + RulerHeight;
        }

        /// <summary>
        /// The host clip under <paramref name="point"/>, or null. A hit inside a slim lane returns the
        /// host's clip on purpose: the lane is part of the video row, so dragging there moves the clip
        /// and its sound together instead of pretending the audio can be pulled out of the row.
        /// </summary>
        private TimelineSegment? SegmentAt(TimelineRow row, Point point)
        {
            foreach (TimelineSegment segment in row.Track.Segments)
            {
                Rect rect = SegmentRect(segment, row.Top, row.HostHeight);
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

            if (IsRuler(point.Y))
            {
                isScrubbing = true;
                CaptureMouse();
                SeekRequested?.Invoke(this, SecondsAt(point));
                e.Handled = true;
                return;
            }

            TimelineRow? row = RowAt(point);
            if (tracks == null || row == null)
            {
                SeekRequested?.Invoke(this, SecondsAt(point));
                e.Handled = true;
                return;
            }

            TimelineTrack track = row.Track;
            bool controlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (point.X < HeaderWidth)
            {
                TrackMouseDown?.Invoke(this, new TrackMouseDownEventArgs(track, controlPressed, SecondsAt(point), e.ClickCount));

                rowDragFromIndex = tracks.IndexOf(track);
                rowDragInsertIndex = -1;
                rowDragStartPoint = point;
                isRowDragging = false;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            TimelineSegment? segment = SegmentAt(row, point);
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
                rowDragInsertIndex = InsertIndexAt(point.Y, out double insertLineY);
                rowDragInsertLineY = insertLineY;
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
            int rowIndex = -1;
            TimelineRow? row = RowAt(point);
            if (row != null && tracks != null)
            {
                rowIndex = tracks.IndexOf(row.Track);
            }

            double seconds = SecondsAt(point);
            e.Handled = true;

            AudioFilesDropped?.Invoke(this, new AudioFilesDroppedEventArgs(paths, rowIndex, seconds));
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            Focus();

            Point point = e.GetPosition(this);
            TimelineRow? row = RowAt(point);
            if (row == null)
            {
                return;
            }

            // The header names the row and empty lane space belongs to the row as a whole, so only a
            // hit on a clip itself is offered the clip menu. SegmentAt resolves a hit inside a slim
            // lane to the host's clip on purpose, which is what makes a right-click on the waveform
            // behave like one on the picture whose sound it is.
            TimelineSegment? segment = point.X < HeaderWidth ? null : SegmentAt(row, point);
            if (segment != null)
            {
                SegmentContextMenuRequested?.Invoke(this, new TimelineSegmentContextEventArgs(row.Track, segment));
                e.Handled = true;
                return;
            }

            TrackContextMenuRequested?.Invoke(this, row.Track);
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

            List<TimelineRow> rows = BuildRows();
            TimelineRow? row = RowAt(rows, point);
            if (row != null)
            {
                TimelineTrack candidate = row.Track;
                if (candidate.Kind == segment.Kind)
                {
                    segmentDropKind = TrackDropKind.ExistingTrack;
                    segmentDropTrack = candidate;
                    segmentDropRowIndex = tracks.IndexOf(candidate);
                    return;
                }

                // Wrong kind: dropping between rows is still allowed. The insertion point is the
                // candidate's own index in the collection, before it or right after it.
                segmentDropKind = TrackDropKind.NewTrack;
                int candidateIndex = tracks.IndexOf(candidate);
                segmentDropRowIndex = point.Y < row.Top + (row.Height / 2.0) ? candidateIndex : candidateIndex + 1;
                return;
            }

            segmentDropKind = TrackDropKind.NewTrack;
            segmentDropRowIndex = InsertIndexAt(point.Y, out _);
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
            // An attachment appearing or leaving folds a lane into a row or takes it out again, so
            // this is the one track property that moves rows instead of only repainting them.
            if (string.Equals(e.PropertyName, nameof(TimelineTrack.AttachedHostTrack), StringComparison.Ordinal))
            {
                InvalidateMeasure();
            }

            InvalidateVisual();
        }

        private void OnSegmentChanged(object? sender, PropertyChangedEventArgs e)
        {
            InvalidateVisual();
        }
    }
}
