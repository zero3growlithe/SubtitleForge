using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SubtitleForge.Models;
using SubtitleForge.Services;

namespace SubtitleForge.Controls
{
    /// <summary>Two hex colours plus a gradient direction, with a live swatch.</summary>
    public partial class ColorSpecPicker : UserControl
    {
        private bool suppress;

        public ColorSpecPicker()
        {
            InitializeComponent();
            UpdateSwatch();
        }

        public event EventHandler? ValueChanged;

        public void Load(ColorSpec? spec)
        {
            suppress = true;
            try
            {
                ColorSpec source = spec ?? ColorSpec.Solid("#FFFFFFFF");
                StartBox.Text = source.StartColor;
                EndBox.Text = source.EndColor;
                SelectDirection(source.Direction);
            }
            finally
            {
                suppress = false;
            }

            UpdateSwatch();
        }

        public ColorSpec Capture()
        {
            ColorSpec spec = new ColorSpec();
            spec.StartColor = string.IsNullOrWhiteSpace(StartBox.Text) ? "#FFFFFFFF" : StartBox.Text.Trim();
            spec.EndColor = string.IsNullOrWhiteSpace(EndBox.Text) ? spec.StartColor : EndBox.Text.Trim();

            ComboBoxItem? item = DirectionCombo.SelectedItem as ComboBoxItem;
            string? direction = item == null ? null : item.Tag as string;
            spec.Direction = string.IsNullOrWhiteSpace(direction) ? ColorSpec.DirectionSolid : direction;

            return spec;
        }

        private void SelectDirection(string? direction)
        {
            string value = string.IsNullOrEmpty(direction) ? ColorSpec.DirectionSolid : direction;
            for (int index = 0; index < DirectionCombo.Items.Count; index++)
            {
                ComboBoxItem? item = DirectionCombo.Items[index] as ComboBoxItem;
                if (item != null && string.Equals(item.Tag as string, value, StringComparison.Ordinal))
                {
                    DirectionCombo.SelectedIndex = index;
                    return;
                }
            }

            DirectionCombo.SelectedIndex = 0;
        }

        private void DirectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppress)
            {
                return;
            }

            UpdateSwatch();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private void FieldChanged(object sender, RoutedEventArgs e)
        {
            if (suppress)
            {
                return;
            }

            UpdateSwatch();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateSwatch()
        {
            ColorSpec spec = Capture();
            Rect bounds = new Rect(0.0, 0.0, Math.Max(40.0, Swatch.ActualWidth), 20.0);
            Brush? brush = StyleBrushes.CreateBrush(spec, bounds, 1.0);
            Swatch.Background = brush ?? Brushes.Transparent;
        }
    }
}
