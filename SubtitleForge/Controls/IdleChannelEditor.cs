using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SubtitleForge.Models;

namespace SubtitleForge.Controls
{
    /// <summary>One row of idle-animation controls: on/off, amplitude, frequency and phase.</summary>
    public sealed class IdleChannelEditor : Grid
    {
        private readonly CheckBox enabledCheck = new CheckBox();
        private readonly TextBox amplitudeBox = new TextBox();
        private readonly TextBox frequencyBox = new TextBox();
        private readonly TextBox phaseBox = new TextBox();

        private bool suppress;

        public IdleChannelEditor(string title, string amplitudeHint)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150.0) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52.0) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52.0) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52.0) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            Margin = new Thickness(0.0, 3.0, 0.0, 3.0);

            enabledCheck.Content = title;
            enabledCheck.VerticalAlignment = VerticalAlignment.Center;
            enabledCheck.MinWidth = 140.0;
            enabledCheck.Click += OnFieldChanged;
            SetColumn(enabledCheck, 0);
            Children.Add(enabledCheck);

            AddLabel("Amplitude", 1, amplitudeHint);
            PrepareBox(amplitudeBox, 2, amplitudeHint);
            AddLabel("Frequency", 3, "Cycles per second");
            PrepareBox(frequencyBox, 4, "Cycles per second");
            AddLabel("Phase", 5, "Phase offset in degrees");
            PrepareBox(phaseBox, 6, "Phase offset in degrees");
        }

        public event EventHandler? Changed;

        public IdleChannel? Channel { get; private set; }

        public void Load(IdleChannel channel)
        {
            Channel = channel;
            suppress = true;
            try
            {
                enabledCheck.IsChecked = channel.Enabled;
                amplitudeBox.Text = channel.Amplitude.ToString("0.###", CultureInfo.InvariantCulture);
                frequencyBox.Text = channel.Frequency.ToString("0.###", CultureInfo.InvariantCulture);
                phaseBox.Text = channel.Phase.ToString("0.#", CultureInfo.InvariantCulture);
            }
            finally
            {
                suppress = false;
            }
        }

        private void AddLabel(string text, int column, string tooltip)
        {
            TextBlock label = new TextBlock();
            label.Text = text;
            label.Margin = new Thickness(column % 2 == 1 ? 8.0 : 4.0, 0.0, 4.0, 0.0);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.ToolTip = tooltip;
            if (TryFindResource("SectionLabelStyle") is Style style)
            {
                label.Style = style;
            }

            SetColumn(label, column);
            Children.Add(label);
        }

        private void PrepareBox(TextBox box, int column, string tooltip)
        {
            box.VerticalContentAlignment = VerticalAlignment.Center;
            box.ToolTip = tooltip;
            box.Margin = new Thickness(0.0, 0.0, 4.0, 0.0);
            box.LostFocus += OnFieldChanged;
            SetColumn(box, column);
            Children.Add(box);
        }

        private void OnFieldChanged(object sender, RoutedEventArgs e)
        {
            if (suppress || Channel == null)
            {
                return;
            }

            Channel.Enabled = enabledCheck.IsChecked == true;
            Channel.Amplitude = ParseDouble(amplitudeBox.Text, Channel.Amplitude, 0.0, 100.0);
            Channel.Frequency = ParseDouble(frequencyBox.Text, Channel.Frequency, 0.0, 40.0);
            Channel.Phase = ParseDouble(phaseBox.Text, Channel.Phase, -720.0, 720.0);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static double ParseDouble(string? text, double fallback, double minimum, double maximum)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            double value;
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return fallback;
            }

            if (value < minimum)
            {
                value = minimum;
            }

            if (value > maximum)
            {
                value = maximum;
            }

            return value;
        }
    }
}
