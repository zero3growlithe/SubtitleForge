using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SubtitleForge.Services;

namespace SubtitleForge.Controls
{
    /// <summary>
    /// Colour picker with a saturation/value square, a hue ramp, an alpha ramp, preset swatches
    /// and an optional hex field. Every change is reported live so callers can repaint a preview
    /// while the dialog is still open.
    /// </summary>
    public partial class ColorPickerWindow : Window
    {
        private static readonly string[] PresetColors = new string[]
        {
            "#FFFFFFFF", "#FFD6DCE6", "#FF9AA0AC", "#FF5A5F6B",
            "#FF000000", "#FF1B1B1F", "#FF2F81F7", "#FF4AA3FF",
            "#FFE5484D", "#FFFF7A45", "#FFFFD54A", "#FF46C93A",
            "#FF2ED9C3", "#FF3B82F6", "#FF8B5CF6", "#FFE44FA8",
            "#80FFFFFF", "#80000000", "#00000000"
        };

        private double hue = 0.0;
        private double saturation = 1.0;
        private double value = 1.0;
        private double alpha = 1.0;

        private bool draggingSv;
        private bool draggingHue;
        private bool draggingAlpha;
        private bool updatingHex;

        public ColorPickerWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        /// <summary>Fires on every change, so a live preview can follow along.</summary>
        public event EventHandler? ColorChanged;

        public Color SelectedColor
        {
            get
            {
                Color color = HsvToColor(hue, saturation, value, alpha);
                return color;
            }
        }

        /// <summary>Shows the picker modally. Returns true when the user accepted a colour.</summary>
        public static bool TryPick(Window? owner, Color initial, out Color result)
        {
            ColorPickerWindow window = new ColorPickerWindow();
            if (owner != null && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }

            window.SetColor(initial);
            bool accepted = window.ShowDialog() == true;
            result = window.SelectedColor;
            return accepted;
        }

        public void SetColor(Color color)
        {
            double h;
            double s;
            double v;
            ColorToHsv(color, out h, out s, out v);
            hue = h;
            saturation = s;
            value = v;
            alpha = color.A / 255.0;
            BuildPresets();
            Refresh();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            Refresh();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (PresetPanel.Children.Count == 0)
            {
                BuildPresets();
            }

            Refresh();
        }

        private void BuildPresets()
        {
            PresetPanel.Children.Clear();
            foreach (string hex in PresetColors)
            {
                Color color = StyleBrushes.ParseColor(hex, Colors.White);
                Button button = new Button();
                button.Width = 24.0;
                button.Height = 22.0;
                button.MinWidth = 0.0;
                button.Padding = new Thickness(0.0);
                button.Margin = new Thickness(0.0, 0.0, 4.0, 4.0);
                button.Tag = hex;
                button.ToolTip = hex;
                button.Background = new SolidColorBrush(color);
                button.Click += PresetClick;
                PresetPanel.Children.Add(button);
            }
        }

        private void PresetClick(object sender, RoutedEventArgs e)
        {
            Button? button = sender as Button;
            if (button == null)
            {
                return;
            }

            string? hex = button.Tag as string;
            if (string.IsNullOrEmpty(hex))
            {
                return;
            }

            SetColor(StyleBrushes.ParseColor(hex, Colors.White));
            ColorChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Saturation / value square ──────────────────────────────────────

        private void SvMouseDown(object sender, MouseButtonEventArgs e)
        {
            draggingSv = true;
            SvGrid.CaptureMouse();
            ApplySv(e.GetPosition(SvGrid));
            e.Handled = true;
        }

        private void SvMouseMove(object sender, MouseEventArgs e)
        {
            if (!draggingSv)
            {
                return;
            }

            ApplySv(e.GetPosition(SvGrid));
            e.Handled = true;
        }

        private void SvMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!draggingSv)
            {
                return;
            }

            draggingSv = false;
            SvGrid.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ApplySv(Point position)
        {
            double width = Math.Max(1.0, SvGrid.ActualWidth);
            double height = Math.Max(1.0, SvGrid.ActualHeight);

            saturation = Math.Max(0.0, Math.Min(1.0, position.X / width));
            value = 1.0 - Math.Max(0.0, Math.Min(1.0, position.Y / height));
            Refresh();
            ColorChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Hue ramp ───────────────────────────────────────────────────────

        private void HueMouseDown(object sender, MouseButtonEventArgs e)
        {
            draggingHue = true;
            HueGrid.CaptureMouse();
            ApplyHue(e.GetPosition(HueGrid));
            e.Handled = true;
        }

        private void HueMouseMove(object sender, MouseEventArgs e)
        {
            if (!draggingHue)
            {
                return;
            }

            ApplyHue(e.GetPosition(HueGrid));
            e.Handled = true;
        }

        private void HueMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!draggingHue)
            {
                return;
            }

            draggingHue = false;
            HueGrid.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ApplyHue(Point position)
        {
            double width = Math.Max(1.0, HueGrid.ActualWidth);
            hue = Math.Max(0.0, Math.Min(0.9999, position.X / width)) * 360.0;
            Refresh();
            ColorChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Alpha ramp ─────────────────────────────────────────────────────

        private void AlphaMouseDown(object sender, MouseButtonEventArgs e)
        {
            draggingAlpha = true;
            AlphaGrid.CaptureMouse();
            ApplyAlpha(e.GetPosition(AlphaGrid));
            e.Handled = true;
        }

        private void AlphaMouseMove(object sender, MouseEventArgs e)
        {
            if (!draggingAlpha)
            {
                return;
            }

            ApplyAlpha(e.GetPosition(AlphaGrid));
            e.Handled = true;
        }

        private void AlphaMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!draggingAlpha)
            {
                return;
            }

            draggingAlpha = false;
            AlphaGrid.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ApplyAlpha(Point position)
        {
            double width = Math.Max(1.0, AlphaGrid.ActualWidth);
            alpha = Math.Max(0.0, Math.Min(1.0, position.X / width));
            Refresh();
            ColorChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Hex field ──────────────────────────────────────────────────────

        private void HexKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyHex();
                e.Handled = true;
            }
        }

        private void HexLostFocus(object sender, RoutedEventArgs e)
        {
            ApplyHex();
        }

        private void ApplyHex()
        {
            if (updatingHex)
            {
                return;
            }

            string text = (HexBox.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return;
            }

            if (!text.StartsWith("#", StringComparison.Ordinal))
            {
                text = "#" + text;
            }

            Color parsed = StyleBrushes.ParseColor(text, SelectedColor);
            SetColor(parsed);
            ColorChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Painting ───────────────────────────────────────────────────────

        private void Refresh()
        {
            Color color = SelectedColor;
            Color opaque = Color.FromRgb(color.R, color.G, color.B);

            SvHueRect.Fill = new SolidColorBrush(HsvToColor(hue, 1.0, 1.0, 1.0));

            double svWidth = Math.Max(1.0, SvGrid.ActualWidth);
            double svHeight = Math.Max(1.0, SvGrid.ActualHeight);
            Canvas.SetLeft(SvCursor, (saturation * svWidth) - 6.0);
            Canvas.SetTop(SvCursor, ((1.0 - value) * svHeight) - 6.0);

            double hueWidth = Math.Max(1.0, HueGrid.ActualWidth);
            Canvas.SetLeft(HueCursor, ((hue / 360.0) * hueWidth) - 2.0);
            Canvas.SetTop(HueCursor, 0.0);

            LinearGradientBrush alphaBrush = new LinearGradientBrush();
            alphaBrush.StartPoint = new Point(0.0, 0.5);
            alphaBrush.EndPoint = new Point(1.0, 0.5);
            alphaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0.0));
            alphaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, color.R, color.G, color.B), 1.0));
            AlphaGradientRect.Fill = alphaBrush;

            double alphaWidth = Math.Max(1.0, AlphaGrid.ActualWidth);
            Canvas.SetLeft(AlphaCursor, (alpha * alphaWidth) - 2.0);
            Canvas.SetTop(AlphaCursor, 0.0);

            PreviewSwatch.Background = new SolidColorBrush(color);
            AlphaText.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"A {(int)Math.Round(alpha * 255.0)}");

            updatingHex = true;
            try
            {
                HexBox.Text = FormatHex(color);
            }
            finally
            {
                updatingHex = false;
            }

            Title = "Pick a colour — " + FormatHex(opaque);
        }

        private static string FormatHex(Color color)
        {
            if (color.A == 255)
            {
                return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
            }

            return "#" + color.A.ToString("X2") + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }

        private void OkClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ── Colour space helpers ───────────────────────────────────────────

        private static Color HsvToColor(double hueDegrees, double saturation01, double value01, double alpha01)
        {
            double h = hueDegrees / 60.0;
            if (h >= 6.0)
            {
                h = 5.9999;
            }

            double s = Math.Max(0.0, Math.Min(1.0, saturation01));
            double v = Math.Max(0.0, Math.Min(1.0, value01));

            int sector = (int)Math.Floor(h);
            double fraction = h - sector;
            double p = v * (1.0 - s);
            double q = v * (1.0 - (s * fraction));
            double t = v * (1.0 - (s * (1.0 - fraction)));

            double r = 0.0;
            double g = 0.0;
            double b = 0.0;

            switch (sector)
            {
                case 0:
                    r = v;
                    g = t;
                    b = p;
                    break;
                case 1:
                    r = q;
                    g = v;
                    b = p;
                    break;
                case 2:
                    r = p;
                    g = v;
                    b = t;
                    break;
                case 3:
                    r = p;
                    g = q;
                    b = v;
                    break;
                case 4:
                    r = t;
                    g = p;
                    b = v;
                    break;
                default:
                    r = v;
                    g = p;
                    b = q;
                    break;
            }

            byte alpha = (byte)Math.Round(Math.Max(0.0, Math.Min(1.0, alpha01)) * 255.0);
            return Color.FromArgb(alpha, ToByte(r), ToByte(g), ToByte(b));
        }

        private static void ColorToHsv(Color color, out double hueDegrees, out double saturation01, out double value01)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double maximum = Math.Max(r, Math.Max(g, b));
            double minimum = Math.Min(r, Math.Min(g, b));
            double delta = maximum - minimum;

            hueDegrees = 0.0;
            if (delta > 0.00001)
            {
                if (maximum == r)
                {
                    hueDegrees = 60.0 * (((g - b) / delta) % 6.0);
                }
                else if (maximum == g)
                {
                    hueDegrees = 60.0 * (((b - r) / delta) + 2.0);
                }
                else
                {
                    hueDegrees = 60.0 * (((r - g) / delta) + 4.0);
                }
            }

            if (hueDegrees < 0.0)
            {
                hueDegrees += 360.0;
            }

            saturation01 = maximum <= 0.00001 ? 0.0 : delta / maximum;
            value01 = maximum;
        }

        private static byte ToByte(double fraction)
        {
            double scaled = Math.Round(Math.Max(0.0, Math.Min(1.0, fraction)) * 255.0);
            return (byte)scaled;
        }
    }
}
