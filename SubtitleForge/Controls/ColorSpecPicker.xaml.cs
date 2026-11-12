using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SubtitleForge.Models;
using SubtitleForge.Services;

namespace SubtitleForge.Controls
{
    /// <summary>
    /// A colour pair plus a gradient direction: two clickable swatches that open the colour
    /// picker (the hex field lives inside the picker, not here) and a direction drop-down.
    /// </summary>
    public partial class ColorSpecPicker : UserControl
    {
        private ColorSpec spec = ColorSpec.Solid("#FFFFFFFF");
        private bool suppress;

        public ColorSpecPicker()
        {
            InitializeComponent();
            UpdateSwatches();
        }

        public event EventHandler? ValueChanged;

        public void Load(ColorSpec? value)
        {
            suppress = true;
            try
            {
                spec = value == null ? ColorSpec.Solid("#FFFFFFFF") : value.Clone();
                SelectDirection(spec.Direction);
            }
            finally
            {
                suppress = false;
            }

            UpdateSwatches();
        }

        public ColorSpec Capture()
        {
            ColorSpec copy = spec.Clone();
            ComboBoxItem? item = DirectionCombo.SelectedItem as ComboBoxItem;
            string? direction = item == null ? null : item.Tag as string;
            copy.Direction = string.IsNullOrWhiteSpace(direction) ? ColorSpec.DirectionSolid : direction;
            if (!copy.IsGradient)
            {
                copy.EndColor = copy.StartColor;
            }

            return copy;
        }

        private void StartClick(object sender, RoutedEventArgs e)
        {
            Color current = StyleBrushes.ParseColor(spec.StartColor, Colors.White);
            Color picked;
            if (!ColorPickerWindow.TryPick(Window.GetWindow(this), current, out picked))
            {
                return;
            }

            spec.StartColor = ToHex(picked);
            UpdateSwatches();
            RaiseChanged();
        }

        private void EndClick(object sender, RoutedEventArgs e)
        {
            Color current = StyleBrushes.ParseColor(spec.EndColor, Colors.White);
            Color picked;
            if (!ColorPickerWindow.TryPick(Window.GetWindow(this), current, out picked))
            {
                return;
            }

            spec.EndColor = ToHex(picked);
            UpdateSwatches();
            RaiseChanged();
        }

        private void SwapClick(object sender, RoutedEventArgs e)
        {
            string first = spec.StartColor;
            spec.StartColor = spec.EndColor;
            spec.EndColor = first;
            UpdateSwatches();
            RaiseChanged();
        }

        private void DirectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSwatches();
            if (suppress)
            {
                return;
            }

            RaiseChanged();
        }

        private void RaiseChanged()
        {
            if (suppress)
            {
                return;
            }

            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SelectDirection(string? direction)
        {
            string wanted = string.IsNullOrEmpty(direction) ? ColorSpec.DirectionSolid : direction;
            for (int index = 0; index < DirectionCombo.Items.Count; index++)
            {
                ComboBoxItem? item = DirectionCombo.Items[index] as ComboBoxItem;
                if (item != null && string.Equals(item.Tag as string, wanted, StringComparison.Ordinal))
                {
                    DirectionCombo.SelectedIndex = index;
                    return;
                }
            }

            DirectionCombo.SelectedIndex = 0;
        }

        private void UpdateSwatches()
        {
            string? direction = (DirectionCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            bool gradient = !string.IsNullOrEmpty(direction)
                && !string.Equals(direction, ColorSpec.DirectionSolid, StringComparison.Ordinal);

            StartSwatch.Background = new SolidColorBrush(StyleBrushes.ParseColor(spec.StartColor, Colors.White));
            EndSwatch.Background = new SolidColorBrush(StyleBrushes.ParseColor(spec.EndColor, Colors.White));
            EndButton.IsEnabled = gradient;
            SwapButton.IsEnabled = gradient;

            if (gradient)
            {
                Rect bounds = new Rect(0.0, 0.0, 44.0, 16.0);
                StartSwatch.Background = StyleBrushes.CreateBrush(Capture(), bounds, 1.0) ?? StartSwatch.Background;
            }
        }

        private static string ToHex(Color color)
        {
            if (color.A == 255)
            {
                return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
            }

            return "#" + color.A.ToString("X2") + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }
    }
}
