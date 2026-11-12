using System;
using System.Windows;
using System.Windows.Controls;
using SubtitleForge.Models;

namespace SubtitleForge.Controls
{
    /// <summary>One row of idle-animation controls: on/off, amplitude, frequency and phase.</summary>
    public sealed class IdleChannelEditor : Grid
    {
        private readonly CheckBox enabledCheck = new CheckBox();
        private readonly NumericDragBox amplitudeBox = new NumericDragBox();
        private readonly NumericDragBox frequencyBox = new NumericDragBox();
        private readonly NumericDragBox phaseBox = new NumericDragBox();

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
            PrepareBox(amplitudeBox, 2, amplitudeHint, 0.0, 100.0, 0.005, "0.###");
            AddLabel("Frequency", 3, "Cycles per second");
            PrepareBox(frequencyBox, 4, "Cycles per second", 0.0, 40.0, 0.01, "0.###");
            AddLabel("Phase", 5, "Phase offset in degrees");
            PrepareBox(phaseBox, 6, "Phase offset in degrees", -720.0, 720.0, 1.0, "0.#");
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
                amplitudeBox.SetValueSilently(channel.Amplitude);
                frequencyBox.SetValueSilently(channel.Frequency);
                phaseBox.SetValueSilently(channel.Phase);
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

        private void PrepareBox(NumericDragBox box, int column, string tooltip, double minimum, double maximum, double dragStep, string format)
        {
            box.VerticalContentAlignment = VerticalAlignment.Center;
            box.ToolTip = tooltip + " — drag left/right to scrub, click to type";
            box.Margin = new Thickness(0.0, 0.0, 4.0, 0.0);
            box.Minimum = minimum;
            box.Maximum = maximum;
            box.DragStep = dragStep;
            box.Format = format;
            box.ValueChanged += OnEditorChanged;
            SetColumn(box, column);
            Children.Add(box);
        }

        private void OnEditorChanged(object? sender, EventArgs e)
        {
            OnFieldChanged(sender, EventArgs.Empty);
        }

        private void OnFieldChanged(object sender, EventArgs e)
        {
            if (suppress || Channel == null)
            {
                return;
            }

            Channel.Enabled = enabledCheck.IsChecked == true;
            Channel.Amplitude = amplitudeBox.Value;
            Channel.Frequency = frequencyBox.Value;
            Channel.Phase = phaseBox.Value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
