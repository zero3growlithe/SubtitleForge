using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SubtitleForge.Models
{
    /// <summary>
    /// One row of the multitrack timeline. A track owns a name and a colour plus the
    /// segments placed on its lane; segments of a lane never overlap.
    /// </summary>
    public class TimelineTrack : INotifyPropertyChanged
    {
        private string name;
        private Color backgroundColor;
        private bool isSelected;

        public TimelineTrack(TrackKind kind, string name, Color backgroundColor)
        {
            Kind = kind;
            this.name = name;
            this.backgroundColor = backgroundColor;
            Segments = new ObservableCollection<TimelineSegment>();
            Segments.CollectionChanged += OnSegmentsChanged;
        }

        public TrackKind Kind { get; }

        public ObservableCollection<TimelineSegment> Segments { get; }

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
                    OnPropertyChanged(nameof(LaneBrush));
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
        /// Lane colour — the empty part of the row, a darker shade of the segment colour.
        /// </summary>
        public Brush LaneBrush
        {
            get
            {
                Color lane = Color.FromRgb(
                    (byte)(backgroundColor.R * 0.5),
                    (byte)(backgroundColor.G * 0.5),
                    (byte)(backgroundColor.B * 0.5));
                SolidColorBrush brush = new SolidColorBrush(lane);
                brush.Freeze();
                return brush;
            }
        }

        /// <summary>
        /// Waveform and label colour, derived from the background so it always contrasts.
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
        /// Track-level selection — the whole row is selected by clicking its header.
        /// </summary>
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

        public bool HasSegments
        {
            get { return Segments.Count > 0; }
        }

        public double ContentStartSeconds
        {
            get
            {
                double start = 0.0;
                bool first = true;
                foreach (TimelineSegment segment in Segments)
                {
                    if (first || segment.StartSeconds < start)
                    {
                        start = segment.StartSeconds;
                        first = false;
                    }
                }

                return start;
            }
        }

        public double ContentEndSeconds
        {
            get
            {
                double end = 0.0;
                foreach (TimelineSegment segment in Segments)
                {
                    if (segment.EndSeconds > end)
                    {
                        end = segment.EndSeconds;
                    }
                }

                return end;
            }
        }

        public double ContentDurationSeconds
        {
            get { return ContentEndSeconds - ContentStartSeconds; }
        }

        public string RangeText
        {
            get
            {
                if (Segments.Count == 0)
                {
                    return "empty";
                }

                if (Segments.Count == 1)
                {
                    return TimelineSegment.FormatSeconds(Segments[0].StartSeconds)
                        + " – "
                        + TimelineSegment.FormatSeconds(Segments[0].EndSeconds);
                }

                return Segments.Count.ToString()
                    + " segments · "
                    + TimelineSegment.FormatSeconds(ContentStartSeconds)
                    + " – "
                    + TimelineSegment.FormatSeconds(ContentEndSeconds);
            }
        }

        public TimelineSegment? SegmentAt(double seconds)
        {
            foreach (TimelineSegment segment in Segments)
            {
                if (segment.Contains(seconds))
                {
                    return segment;
                }
            }

            return null;
        }

        /// <summary>
        /// Segments ordered by position — the order every placement algorithm needs.
        /// </summary>
        public List<TimelineSegment> SegmentsByStart()
        {
            List<TimelineSegment> ordered = new List<TimelineSegment>(Segments);
            ordered.Sort(delegate (TimelineSegment left, TimelineSegment right)
            {
                return left.StartSeconds.CompareTo(right.StartSeconds);
            });
            return ordered;
        }

        public List<TimelineSegment> SelectedSegments()
        {
            List<TimelineSegment> selected = new List<TimelineSegment>();
            foreach (TimelineSegment segment in Segments)
            {
                if (segment.IsSelected)
                {
                    selected.Add(segment);
                }
            }

            return selected;
        }

        public TimelineSegment? FirstSegment()
        {
            return Segments.Count == 0 ? null : Segments[0];
        }

        /// <summary>
        /// Moves anything overlapping <paramref name="segment"/> to its right edge, cascading
        /// along the lane so nothing overlaps afterwards.
        /// </summary>
        public void PushAside(TimelineSegment segment)
        {
            double floor = segment.EndSeconds;

            foreach (TimelineSegment other in SegmentsByStart())
            {
                if (ReferenceEquals(other, segment))
                {
                    continue;
                }

                if (other.EndSeconds <= segment.StartSeconds)
                {
                    continue;
                }

                if (other.StartSeconds < floor)
                {
                    other.StartSeconds = floor;
                }

                if (other.EndSeconds > floor)
                {
                    floor = other.EndSeconds;
                }
            }
        }

        public void ClearSegmentSelection()
        {
            foreach (TimelineSegment segment in Segments)
            {
                segment.IsSelected = false;
            }
        }

        public void SelectAllSegments(bool selected)
        {
            foreach (TimelineSegment segment in Segments)
            {
                segment.IsSelected = selected;
            }
        }

        private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Segments));
            OnPropertyChanged(nameof(HasSegments));
            NotifyContentChanged();
        }

        /// <summary>
        /// Lets the timeline redraw when a segment on this lane moves or changes length.
        /// </summary>
        public void NotifyContentChanged()
        {
            OnPropertyChanged(nameof(ContentStartSeconds));
            OnPropertyChanged(nameof(ContentEndSeconds));
            OnPropertyChanged(nameof(ContentDurationSeconds));
            OnPropertyChanged(nameof(RangeText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
