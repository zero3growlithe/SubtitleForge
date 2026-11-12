using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SubtitleForge.Controls
{
    /// <summary>
    /// Numeric field with Unity-style scrubbing: press and drag left/right to change the value,
    /// click once to type a value instead. Hold Shift while dragging for fine steps and Ctrl for
    /// coarse ones. An empty text is a valid state when <see cref="AllowEmpty"/> is set, which is
    /// how the cue overrides express "use the default".
    /// </summary>
    public class NumericDragBox : TextBox
    {
        private Point dragOrigin;
        private double dragStartValue;
        private bool dragCandidate;
        private bool dragging;
        private bool updatingText;
        private bool mixed;
        private double lastValidValue;

        public NumericDragBox()
        {
            VerticalContentAlignment = VerticalAlignment.Center;
            TextAlignment = TextAlignment.Left;
            Minimum = double.MinValue;
            Maximum = double.MaxValue;
            DragStep = 0.01;
            Format = "0.###";
            DragStepMode = DragStepModes.Relative;
        }

        /// <summary>Raised whenever the value changes, whether by dragging or by typing.</summary>
        public event EventHandler? ValueChanged;

        public double Minimum { get; set; }

        public double Maximum { get; set; }

        /// <summary>
        /// Pixels-to-value ratio. In <see cref="DragStepModes.Relative"/> mode this is the value
        /// delta per pixel; in <see cref="DragStepModes.Scale"/> mode it multiplies the value's own
        /// magnitude, so big numbers move fast and small ones stay precise.
        /// </summary>
        public double DragStep { get; set; }

        public DragStepModes DragStepMode { get; set; }

        /// <summary>Zero-based format string used when the text is written back.</summary>
        public string Format { get; set; }

        /// <summary>When true, leaving the field empty reports <see cref="IsEmpty"/> instead of a value.</summary>
        public bool AllowEmpty { get; set; }

        public bool IsEmpty
        {
            get { return string.IsNullOrWhiteSpace(Text); }
        }

        /// <summary>
        /// True while the field shows the "several different values" placeholder. The value is
        /// meaningless until the user types or drags, which is what clears it.
        /// </summary>
        public bool IsMixed
        {
            get { return mixed; }
        }

        public double Value
        {
            get
            {
                double parsed;
                if (!double.TryParse((Text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    return Minimum > double.MinValue ? Minimum : 0.0;
                }

                return Clamp(parsed);
            }
            set
            {
                SetValueSilently(Clamp(value));
            }
        }

        public int IntValue
        {
            get { return (int)Math.Round(Value); }
            set { Value = value; }
        }

        /// <summary>Writes a value without raising <see cref="ValueChanged"/>.</summary>
        public void SetValueSilently(double value)
        {
            updatingText = true;
            try
            {
                mixed = false;
                lastValidValue = Clamp(value);
                Text = lastValidValue.ToString(Format, CultureInfo.InvariantCulture);
            }
            finally
            {
                updatingText = false;
            }
        }

        /// <summary>
        /// Shows the multi-selection placeholder without raising <see cref="ValueChanged"/>; the
        /// field stops reporting a value until the user changes it.
        /// </summary>
        public void SetMixed()
        {
            updatingText = true;
            try
            {
                mixed = true;
                Text = "\u2014";
            }
            finally
            {
                updatingText = false;
            }
        }

        /// <summary>Empties the field without raising <see cref="ValueChanged"/>.</summary>
        public void ClearSilently()
        {
            updatingText = true;
            try
            {
                mixed = false;
                Text = string.Empty;
            }
            finally
            {
                updatingText = false;
            }
        }

        protected override void OnTextChanged(TextChangedEventArgs e)
        {
            base.OnTextChanged(e);

            if (updatingText)
            {
                return;
            }

            mixed = false;
            double typed;
            if (double.TryParse((Text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out typed))
            {
                lastValidValue = Clamp(typed);
            }

            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Accepts whatever is typed, or puts the last good number back when the text is not a
        /// number at all. Returns true when the field now holds a usable value.
        /// </summary>
        public bool Commit()
        {
            if (mixed && AllowEmpty)
            {
                return false;
            }

            string raw = (Text ?? string.Empty).Trim();
            if (raw.Length == 0 && AllowEmpty)
            {
                return false;
            }

            double parsed;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                // Reject the input: leave the field showing the value it had before the user typed.
                SetValueSilently(lastValidValue);
                return false;
            }

            double clamped = Clamp(parsed);
            bool changed = Math.Abs(clamped - lastValidValue) > 1e-9;
            SetValueSilently(clamped);
            if (changed)
            {
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }

            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                Commit();
                // Hand focus back so every LostFocus handler (the "field changed" plumbing) runs.
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                SetValueSilently(lastValidValue);
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);

            if (!IsEnabled)
            {
                return;
            }

            dragOrigin = e.GetPosition(this);
            dragStartValue = Value;
            dragCandidate = true;
            dragging = false;
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);

            if (!dragCandidate || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point current = e.GetPosition(this);
            double deltaX = current.X - dragOrigin.X;
            double deltaY = dragOrigin.Y - current.Y;

            if (!dragging)
            {
                // Unity starts scrubbing only after the pointer really moved, so a plain click
                // still edits the text.
                if (mixed)
                {
                    dragStartValue = Minimum > double.MinValue ? Minimum : 0.0;
                }

                double threshold = Math.Max(2.0, SystemParameters.MinimumHorizontalDragDistance * 0.5);
                if (Math.Abs(deltaX) < threshold && Math.Abs(deltaY) < threshold)
                {
                    return;
                }

                dragging = true;
                if (!IsMouseCaptured)
                {
                    CaptureMouse();
                }
            }

            double pixels = Math.Abs(deltaX) >= Math.Abs(deltaY) ? deltaX : deltaY;
            double step = DragStep;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                step *= 0.1;
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                step *= 10.0;
            }

            double result;
            if (DragStepMode == DragStepModes.Scale)
            {
                double magnitude = Math.Max(1.0, Math.Abs(dragStartValue));
                result = dragStartValue + (pixels * step * magnitude);
            }
            else
            {
                result = dragStartValue + (pixels * step);
            }

            SetValueSilently(result);
            ValueChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);

            bool wasDragging = dragging;
            dragCandidate = false;
            dragging = false;

            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }

            if (wasDragging)
            {
                e.Handled = true;
            }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (!IsEnabled || !IsKeyboardFocusWithin)
            {
                base.OnMouseWheel(e);
                return;
            }

            double step = DragStep * (DragStepMode == DragStepModes.Scale ? Math.Max(1.0, Math.Abs(Value)) : 1.0);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                step *= 0.1;
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                step *= 10.0;
            }

            double direction = e.Delta > 0 ? 1.0 : -1.0;
            SetValueSilently(Value + (step * direction));
            ValueChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            SelectAll();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);

            if (updatingText || mixed || (AllowEmpty && IsEmpty))
            {
                return;
            }

            // Reformat whatever was typed so the field always shows a parsable number.
            SetValueSilently(Value);
        }

        private double Clamp(double value)
        {
            if (value < Minimum)
            {
                return Minimum;
            }

            if (value > Maximum)
            {
                return Maximum;
            }

            return value;
        }
    }

    public enum DragStepModes
    {
        /// <summary>DragStep is a value delta per pixel.</summary>
        Relative,

        /// <summary>DragStep is a fraction of the current value per pixel.</summary>
        Scale
    }
}
