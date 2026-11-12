using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SubtitleForge.Models;

namespace SubtitleForge.Services
{
    /// <summary>Turns the profile colour/paint descriptions into WPF brushes.</summary>
    public static class StyleBrushes
    {
        public static Color ParseColor(string? text, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            string value = text.Trim();
            if (value.StartsWith("#", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }

            if (value.Length == 6)
            {
                value = "FF" + value;
            }

            if (value.Length != 8)
            {
                return fallback;
            }

            uint packed;
            if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out packed))
            {
                return fallback;
            }

            return Color.FromArgb(
                (byte)((packed >> 24) & 0xFF),
                (byte)((packed >> 16) & 0xFF),
                (byte)((packed >> 8) & 0xFF),
                (byte)(packed & 0xFF));
        }

        public static Brush? CreateBrush(ColorSpec? spec, Rect bounds, double opacity)
        {
            if (spec == null)
            {
                return null;
            }

            Color start = ParseColor(spec.StartColor, Colors.White);
            Color end = ParseColor(spec.EndColor, start);

            double alphaFactor = opacity < 0.0 ? 0.0 : (opacity > 1.0 ? 1.0 : opacity);
            start = ApplyOpacity(start, alphaFactor);
            end = ApplyOpacity(end, alphaFactor);

            if (!spec.IsGradient)
            {
                SolidColorBrush solid = new SolidColorBrush(start);
                solid.Freeze();
                return solid;
            }

            Point startPoint;
            Point endPoint;
            ResolveDirection(spec.Direction, bounds, out startPoint, out endPoint);

            LinearGradientBrush gradient = new LinearGradientBrush(start, end, startPoint, endPoint);
            gradient.MappingMode = BrushMappingMode.Absolute;
            gradient.Freeze();
            return gradient;
        }

        public static Color ApplyOpacity(Color color, double opacity)
        {
            double alpha = color.A * opacity;
            if (alpha < 0.0)
            {
                alpha = 0.0;
            }

            if (alpha > 255.0)
            {
                alpha = 255.0;
            }

            return Color.FromArgb((byte)Math.Round(alpha), color.R, color.G, color.B);
        }

        public static void ResolveDirection(string? direction, Rect bounds, out Point start, out Point end)
        {
            double midX = bounds.Left + (bounds.Width * 0.5);
            double midY = bounds.Top + (bounds.Height * 0.5);
            string value = string.IsNullOrEmpty(direction) ? ColorSpec.DirectionSolid : direction;

            if (string.Equals(value, ColorSpec.DirectionLeftToRight, StringComparison.OrdinalIgnoreCase))
            {
                start = new Point(bounds.Left, midY);
                end = new Point(bounds.Right, midY);
                return;
            }

            if (string.Equals(value, ColorSpec.DirectionRightToLeft, StringComparison.OrdinalIgnoreCase))
            {
                start = new Point(bounds.Right, midY);
                end = new Point(bounds.Left, midY);
                return;
            }

            if (string.Equals(value, ColorSpec.DirectionTopToBottom, StringComparison.OrdinalIgnoreCase))
            {
                start = new Point(midX, bounds.Top);
                end = new Point(midX, bounds.Bottom);
                return;
            }

            if (string.Equals(value, ColorSpec.DirectionBottomToTop, StringComparison.OrdinalIgnoreCase))
            {
                start = new Point(midX, bounds.Bottom);
                end = new Point(midX, bounds.Top);
                return;
            }

            if (string.Equals(value, ColorSpec.DirectionDownRight, StringComparison.OrdinalIgnoreCase))
            {
                start = new Point(bounds.Left, bounds.Top);
                end = new Point(bounds.Right, bounds.Bottom);
                return;
            }

            if (string.Equals(value, ColorSpec.DirectionUpRight, StringComparison.OrdinalIgnoreCase))
            {
                start = new Point(bounds.Left, bounds.Bottom);
                end = new Point(bounds.Right, bounds.Top);
                return;
            }

            start = new Point(bounds.Left, midY);
            end = new Point(bounds.Right, midY);
        }
    }
}
