using System;

namespace SubtitleForge.Models
{
    /// <summary>
    /// A solid colour or a two-stop gradient with a direction. Stored as hex strings so the
    /// profiles stay readable in JSON.
    /// </summary>
    public sealed class ColorSpec
    {
        public const string DirectionSolid = "Solid";
        public const string DirectionLeftToRight = "LeftToRight";
        public const string DirectionRightToLeft = "RightToLeft";
        public const string DirectionTopToBottom = "TopToBottom";
        public const string DirectionBottomToTop = "BottomToTop";
        public const string DirectionDownRight = "DownRight";
        public const string DirectionUpRight = "UpRight";

        public string StartColor { get; set; } = "#FFFFFFFF";

        public string EndColor { get; set; } = "#FFFFFFFF";

        public string Direction { get; set; } = DirectionSolid;

        public ColorSpec()
        {
        }

        public ColorSpec(string startColor, string endColor, string direction)
        {
            StartColor = startColor;
            EndColor = endColor;
            Direction = direction;
        }

        public bool IsGradient
        {
            get
            {
                if (string.IsNullOrEmpty(EndColor))
                {
                    return false;
                }

                if (string.Equals(Direction, DirectionSolid, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return !string.Equals(StartColor, EndColor, StringComparison.OrdinalIgnoreCase);
            }
        }

        public ColorSpec Clone()
        {
            ColorSpec copy = new ColorSpec();
            copy.StartColor = StartColor;
            copy.EndColor = EndColor;
            copy.Direction = Direction;
            return copy;
        }

        public static ColorSpec Solid(string hex)
        {
            return new ColorSpec(hex, hex, DirectionSolid);
        }

        public static ColorSpec Gradient(string startColor, string endColor, string direction)
        {
            return new ColorSpec(startColor, endColor, direction);
        }
    }
}
