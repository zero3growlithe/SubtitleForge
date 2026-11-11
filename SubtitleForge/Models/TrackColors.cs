using System;
using System.Windows.Media;

namespace SubtitleForge.Models
{
    /// <summary>
    /// Track background palette plus the contrast rule for the waveform drawn on top of it.
    /// </summary>
    public static class TrackColors
    {
        private static readonly string[] Names =
        {
            "Slate",
            "Ocean",
            "Moss",
            "Plum",
            "Wine",
            "Amber",
            "Steel",
            "Graphite",
            "Rose",
            "Teal"
        };

        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x2B, 0x3A, 0x55),
            Color.FromRgb(0x1F, 0x3A, 0x5F),
            Color.FromRgb(0x23, 0x40, 0x2E),
            Color.FromRgb(0x40, 0x2C, 0x50),
            Color.FromRgb(0x4E, 0x25, 0x2E),
            Color.FromRgb(0x5A, 0x45, 0x1C),
            Color.FromRgb(0x2E, 0x36, 0x3D),
            Color.FromRgb(0x2A, 0x2A, 0x2E),
            Color.FromRgb(0x4A, 0x2A, 0x3C),
            Color.FromRgb(0x1D, 0x3F, 0x3F)
        };

        public static int Count
        {
            get { return Palette.Length; }
        }

        public static Color VideoDefault
        {
            get { return Color.FromRgb(0x24, 0x33, 0x4A); }
        }

        public static Color AudioDefault
        {
            get { return Palette[2]; }
        }

        public static string GetName(int index)
        {
            if (index < 0 || index >= Names.Length)
            {
                return "Custom";
            }

            return Names[index];
        }

        public static Color GetColor(int index)
        {
            if (index < 0 || index >= Palette.Length)
            {
                return Palette[0];
            }

            return Palette[index];
        }

        public static Color NextAudioColor(int existingAudioTrackCount)
        {
            // Skip the two colours used by default for the first video/audio track.
            int index = 1 + (existingAudioTrackCount % (Palette.Length - 1));
            return Palette[index];
        }

        /// <summary>
        /// Picks near-white or near-black, whichever reads better on the given background.
        /// </summary>
        public static Color Contrasting(Color background)
        {
            double luminance = ((0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B)) / 255.0;
            if (luminance > 0.55)
            {
                return Color.FromRgb(0x14, 0x16, 0x1A);
            }

            return Color.FromRgb(0xF2, 0xF6, 0xFC);
        }
    }
}
