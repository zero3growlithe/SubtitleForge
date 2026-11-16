using System;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Small seeded 2D noise, enough for a dissolve mask: smooth lattice noise with a hashed
    /// gradient per corner, interpolated with a smoothstep curve. No tables are kept, so two
    /// profiles with the same seed always break apart in the same pattern and an export matches
    /// the preview.
    /// </summary>
    internal static class ValueNoise
    {
        /// <summary>Noise in -1..1 for the given point and seed.</summary>
        public static double Sample(double x, double y, int seed)
        {
            int x0 = (int)Math.Floor(x);
            int y0 = (int)Math.Floor(y);
            double fx = x - x0;
            double fy = y - y0;

            double n00 = Corner(x0, y0, seed, fx, fy);
            double n10 = Corner(x0 + 1, y0, seed, fx - 1.0, fy);
            double n01 = Corner(x0, y0 + 1, seed, fx, fy - 1.0);
            double n11 = Corner(x0 + 1, y0 + 1, seed, fx - 1.0, fy - 1.0);

            double sx = Smooth(fx);
            double sy = Smooth(fy);

            double top = n00 + ((n10 - n00) * sx);
            double bottom = n01 + ((n11 - n01) * sx);
            double value = top + ((bottom - top) * sy);
            if (value < -1.0)
            {
                return -1.0;
            }

            return value > 1.0 ? 1.0 : value;
        }

        /// <summary>Noise in 0..1, the form every caller here wants.</summary>
        public static double Sample01(double x, double y, int seed)
        {
            return (Sample(x, y, seed) + 1.0) * 0.5;
        }

        /// <summary>Deterministic 0..1 hash, for per-piece constants that must survive a redraw.</summary>
        public static double Hash01(int a, int b, int seed)
        {
            unchecked
            {
                int h = (a * 374761393) + (b * 668265263) + (seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                double value = (h & 0x7FFFFFFF) / (double)0x7FFFFFFF;
                return value;
            }
        }

        private static double Corner(int x, int y, int seed, double dx, double dy)
        {
            // A gradient pointing in a hashed direction, projected onto the distance to the corner.
            double angle = Hash01(x, y, seed) * Math.PI * 2.0;
            return (Math.Cos(angle) * dx) + (Math.Sin(angle) * dy);
        }

        private static double Smooth(double t)
        {
            return t * t * (3.0 - (2.0 * t));
        }
    }
}
