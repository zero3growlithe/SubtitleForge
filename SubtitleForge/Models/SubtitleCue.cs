using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace SubtitleForge.Models
{
    /// <summary>
    /// One subtitle cue: a time range and the text shown inside it.
    /// </summary>
    public class SubtitleCue : INotifyPropertyChanged
    {
        private int number;
        private TimeSpan start;
        private TimeSpan end;
        private string text;
        private double confidence;
        private string? styleProfileId;
        private string? idleProfileId;
        private string? popInProfileId;
        private double? positionX;
        private double? positionY;
        private double? scale;

        public SubtitleCue(TimeSpan start, TimeSpan end, string text, double confidence = 1.0)
        {
            this.start = start;
            this.end = end;
            this.text = text ?? string.Empty;
            this.confidence = confidence;
        }

        public int Number
        {
            get { return number; }
            set
            {
                if (number != value)
                {
                    number = value;
                    OnPropertyChanged();
                }
            }
        }

        public TimeSpan Start
        {
            get { return start; }
            set
            {
                if (start != value)
                {
                    start = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StartDisplay));
                    OnPropertyChanged(nameof(Duration));
                    OnPropertyChanged(nameof(DurationDisplay));
                }
            }
        }

        public TimeSpan End
        {
            get { return end; }
            set
            {
                if (end != value)
                {
                    end = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EndDisplay));
                    OnPropertyChanged(nameof(Duration));
                    OnPropertyChanged(nameof(DurationDisplay));
                }
            }
        }

        public string Text
        {
            get { return text; }
            set
            {
                string updated = value ?? string.Empty;
                if (text != updated)
                {
                    text = updated;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TextPreview));
                }
            }
        }

        /// <summary>
        /// Average probability reported by the recogniser, 0..1.
        /// </summary>
        public double Confidence
        {
            get { return confidence; }
            set
            {
                if (Math.Abs(confidence - value) > 0.0001)
                {
                    confidence = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ConfidenceDisplay));
                }
            }
        }

        /// <summary>Overrides the default visual style for this cue only (null = default).</summary>
        public string? StyleProfileId
        {
            get { return styleProfileId; }
            set
            {
                if (styleProfileId != value)
                {
                    styleProfileId = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCustomLook));
                }
            }
        }

        /// <summary>Overrides the default idle animation for this cue only (null = default).</summary>
        public string? IdleProfileId
        {
            get { return idleProfileId; }
            set
            {
                if (idleProfileId != value)
                {
                    idleProfileId = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCustomLook));
                }
            }
        }

        /// <summary>Overrides the default pop-in animation for this cue only (null = default).</summary>
        public string? PopInProfileId
        {
            get { return popInProfileId; }
            set
            {
                if (popInProfileId != value)
                {
                    popInProfileId = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCustomLook));
                }
            }
        }

        /// <summary>Horizontal anchor override, 0-1 of the frame width (null = default).</summary>
        public double? PositionX
        {
            get { return positionX; }
            set
            {
                if (positionX != value)
                {
                    positionX = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>Vertical anchor override, 0-1 of the frame height (null = default).</summary>
        public double? PositionY
        {
            get { return positionY; }
            set
            {
                if (positionY != value)
                {
                    positionY = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>Scale override on top of the style's font size (null = default).</summary>
        public double? Scale
        {
            get { return scale; }
            set
            {
                if (scale != value)
                {
                    scale = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// True when the cue does not simply use the defaults: it carries its own style, idle or
        /// pop-in profile. The cue list colours these rows so overridden cues stand out.
        /// </summary>
        public bool HasCustomLook
        {
            get
            {
                return !string.IsNullOrEmpty(styleProfileId)
                    || !string.IsNullOrEmpty(idleProfileId)
                    || !string.IsNullOrEmpty(popInProfileId);
            }
        }

        public TimeSpan Duration
        {
            get { return end > start ? end - start : TimeSpan.Zero; }
        }

        public string StartDisplay
        {
            get { return FormatTimestamp(start); }
        }

        public string EndDisplay
        {
            get { return FormatTimestamp(end); }
        }

        public string DurationDisplay
        {
            get { return Duration.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s"; }
        }

        public string ConfidenceDisplay
        {
            get { return (confidence * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%"; }
        }

        public string TextPreview
        {
            get { return text.Replace("\r", " ").Replace("\n", " "); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public SubtitleCue Clone()
        {
            SubtitleCue copy = new SubtitleCue(start, end, text, confidence);
            copy.Number = number;
            copy.StyleProfileId = styleProfileId;
            copy.IdleProfileId = idleProfileId;
            copy.PopInProfileId = popInProfileId;
            copy.PositionX = positionX;
            copy.PositionY = positionY;
            copy.Scale = scale;
            return copy;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Renders a time span as HH:MM:SS.mmm.
        /// </summary>
        public static string FormatTimestamp(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
            {
                value = TimeSpan.Zero;
            }

            int hours = (int)value.TotalHours;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}:{2:00}.{3:000}",
                hours,
                value.Minutes,
                value.Seconds,
                value.Milliseconds);
        }

        /// <summary>
        /// Accepts 00:00:12.500, 00:00:12,500, 0:00:12 and plain seconds ("12.5").
        /// </summary>
        public static bool TryParseTimestamp(string? input, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            string candidate = input.Trim().Replace(',', '.');
            if (candidate.Contains(":"))
            {
                string[] parts = candidate.Split(':');
                if (parts.Length == 2 || parts.Length == 3)
                {
                    double hours = 0.0;
                    double minutes;
                    double seconds;
                    int offset = parts.Length == 3 ? 1 : 0;
                    if (offset == 1 && !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out hours))
                    {
                        return false;
                    }

                    if (!double.TryParse(parts[offset], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes))
                    {
                        return false;
                    }

                    if (!double.TryParse(parts[offset + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                    {
                        return false;
                    }

                    value = TimeSpan.FromSeconds((hours * 3600.0) + (minutes * 60.0) + seconds);
                    return value >= TimeSpan.Zero;
                }

                return false;
            }

            double totalSeconds;
            if (!double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out totalSeconds))
            {
                return false;
            }

            value = TimeSpan.FromSeconds(totalSeconds);
            return value >= TimeSpan.Zero;
        }
    }
}
