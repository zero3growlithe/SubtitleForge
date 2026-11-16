using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using SubtitleForge.Models;
using SubtitleForge.Services;

namespace SubtitleForge.Controls
{
    /// <summary>
    /// When every group of glyphs starts leaving. The exit is anchored to the end of the cue: the
    /// first group starts a little before it and the last group finishes exactly on it, so a subtitle
    /// never stays on screen after its own end time. A stagger that does not fit is compressed rather
    /// than cut off.
    /// </summary>
    internal struct PopOutSchedule
    {
        public bool Active;

        /// <summary>Length of one group's exit, already compressed to fit the cue.</summary>
        public double Duration;

        /// <summary>Delay between neighbouring groups, already compressed.</summary>
        public double Step;

        /// <summary>Absolute playhead position where the first group starts.</summary>
        public double Begin;

        public static PopOutSchedule Create(PopOutProfile? profile, double cueStartSeconds, double cueEndSeconds, int groupCount)
        {
            PopOutSchedule schedule = new PopOutSchedule();
            if (profile == null || !profile.HasExit)
            {
                return schedule;
            }

            double duration = Math.Max(0.01, profile.DurationSeconds);
            double step = 0.0;
            bool staggered = profile.PerCharacter
                && !string.Equals(profile.StaggerGrouping, StaggerGroupings.Cue, StringComparison.OrdinalIgnoreCase);
            if (staggered)
            {
                step = Math.Max(0.0, profile.CharacterDelaySeconds);
            }

            double groups = Math.Max(1.0, groupCount);
            double rawSpan = duration + (step * (groups - 1.0));

            schedule.Active = true;

            if (PopTiming.IsOutside(profile.Timing))
            {
                // The exit starts the moment the cue's time is up, so the words stay on screen for
                // their whole cue and only then begin to leave. Nothing to compress: the cue length
                // does not bound the animation any more.
                schedule.Duration = duration;
                schedule.Step = step;
                schedule.Begin = cueEndSeconds;
                return schedule;
            }

            double cueLength = Math.Max(0.02, cueEndSeconds - cueStartSeconds);

            // Inside the cue the exit may use at most nine tenths of it: a subtitle that starts
            // disappearing on the frame it appeared would read as a mistake.
            double compress = rawSpan > cueLength * 0.9 ? (cueLength * 0.9) / rawSpan : 1.0;

            schedule.Duration = duration * compress;
            schedule.Step = step * compress;
            schedule.Begin = cueEndSeconds - (rawSpan * compress);
            return schedule;
        }

        /// <summary>0 before this group starts, 1 once its exit has finished.</summary>
        public double ProgressFor(double playheadSeconds, double groupIndex)
        {
            if (!Active)
            {
                return 0.0;
            }

            double start = Begin + (Math.Max(0.0, groupIndex) * Step);
            double progress = (playheadSeconds - start) / Math.Max(0.001, Duration);
            if (progress <= 0.0)
            {
                return 0.0;
            }

            return progress >= 1.0 ? 1.0 : progress;
        }
    }

    /// <summary>One glyph's exit at one moment: how far, how transparent, how squashed, how turned.</summary>
    internal struct PopOutGlyphState
    {
        public bool Active;

        public double Progress;

        public double Opacity;

        public double OffsetX;

        public double OffsetY;

        public double ScaleX;

        public double ScaleY;

        public double Rotation;

        /// <summary>True for dissolve and dust: the glyph is drawn through a piece mask.</summary>
        public bool PieceClip;
    }

    /// <summary>
    /// The pieces a glyph breaks into: a grid of cells with the order they leave in, plus the
    /// particles that are blown away. Built once per glyph per profile shape and cached, because
    /// the grid is what makes a dissolve cheap enough to draw every frame.
    /// </summary>
    internal sealed class PopOutPiecePlan
    {
        public Rect Bounds = Rect.Empty;

        public int Columns;

        public int Rows;

        public Rect[] Cells = Array.Empty<Rect>();

        /// <summary>0..1 per cell: 0 leaves first, 1 last. This is what the origin direction and the noise decide.</summary>
        public double[] CellOrder = Array.Empty<double>();

        public Point[] ParticleStart = Array.Empty<Point>();

        /// <summary>Per particle: the order value of the cell it sits in, so it flies off as that part crumbles.</summary>
        public double[] ParticleOrder = Array.Empty<double>();

        /// <summary>Per particle: a fixed 0..1 draw, so wind spread, speed and spin stay put between frames.</summary>
        public double[] ParticleRandom = Array.Empty<double>();
    }

    /// <summary>Caches the piece plans and their clip geometries, and holds the exit math.</summary>
    internal sealed class PopOutPieceCache
    {
        /// <summary>Distinct dissolve stages. More stages are smoother but build more geometry.</summary>
        private const int Buckets = 12;

        private readonly Dictionary<int, PopOutPiecePlan> plans = new Dictionary<int, PopOutPiecePlan>();

        private readonly Dictionary<int, Geometry?[]> clips = new Dictionary<int, Geometry?[]>();

        private string signature = string.Empty;

        private double planFontSize;

        /// <summary>Drops every cached piece: the layout or the profile shape changed.</summary>
        public void Reset()
        {
            plans.Clear();
            clips.Clear();
            signature = string.Empty;
        }

        public PopOutPiecePlan GetPlan(int glyphIndex, Rect bounds, PopOutProfile profile, double fontSize)
        {
            string wanted = profile.ShapeSignature();
            if (!string.Equals(wanted, signature, StringComparison.Ordinal) || Math.Abs(planFontSize - fontSize) > 0.01)
            {
                Reset();
                signature = wanted;
                planFontSize = fontSize;
            }

            if (plans.TryGetValue(glyphIndex, out PopOutPiecePlan? existing))
            {
                return existing;
            }

            PopOutPiecePlan plan = BuildPlan(bounds, profile);
            plans[glyphIndex] = plan;
            return plan;
        }

        /// <summary>
        /// The part of the glyph that has not crumbled yet, in glyph space. Cached per stage so the
        /// frame only pays for a clip push instead of rebuilding a grid of rectangles.
        /// </summary>
        public Geometry GetClip(int glyphIndex, PopOutPiecePlan plan, double progress)
        {
            int bucket = (int)(Math.Max(0.0, Math.Min(0.9999, progress)) * Buckets);
            if (bucket >= Buckets)
            {
                bucket = Buckets - 1;
            }

            if (!clips.TryGetValue(glyphIndex, out Geometry?[]? stages))
            {
                stages = new Geometry?[Buckets];
                clips[glyphIndex] = stages;
            }

            Geometry? cached = stages[bucket];
            if (cached != null)
            {
                return cached;
            }

            double threshold = (bucket + 1.0) / (Buckets + 1.0);
            GeometryGroup group = new GeometryGroup();
            group.FillRule = FillRule.Nonzero;
            for (int index = 0; index < plan.Cells.Length; index++)
            {
                if (plan.CellOrder[index] <= threshold)
                {
                    continue;
                }

                group.Children.Add(new RectangleGeometry(plan.Cells[index]));
            }

            if (group.Children.Count == 0)
            {
                // Nothing left of the glyph. An empty clip is pushed like any other, which draws
                // nothing at all — returning null instead would draw the glyph whole again.
                stages[bucket] = Geometry.Empty;
                return Geometry.Empty;
            }

            group.Freeze();
            stages[bucket] = group;
            return group;
        }

        /// <summary>The exit of one glyph at one progress value, for the kind the profile names.</summary>
        public static PopOutGlyphState ComputeGlyphState(PopOutProfile profile, double progress, double fontSize)
        {
            PopOutGlyphState state = new PopOutGlyphState();
            state.Active = true;
            state.Progress = progress;
            state.Opacity = 1.0;
            state.ScaleX = 1.0;
            state.ScaleY = 1.0;

            double eased = Ease(progress, profile.EasePower);
            string kind = profile.Kind ?? PopOutKind.None;

            if (string.Equals(kind, PopOutKind.FadeOut, StringComparison.OrdinalIgnoreCase))
            {
                state.Opacity = 1.0 - eased;
                return state;
            }

            if (string.Equals(kind, PopOutKind.SlideOut, StringComparison.OrdinalIgnoreCase))
            {
                ResolveDirection(profile.Direction, out double dirX, out double dirY);
                double distance = Math.Max(0.0, profile.DistanceMultiplier) * fontSize * eased;
                state.OffsetX = dirX * distance;
                state.OffsetY = dirY * distance;
                state.Opacity = FadeTail(eased);
                return state;
            }

            if (string.Equals(kind, PopOutKind.ScaleOut, StringComparison.OrdinalIgnoreCase))
            {
                double target = Math.Abs(profile.EndScale) < 0.001 ? 0.001 : profile.EndScale;
                double scale = 1.0 + ((target - 1.0) * eased);
                double jellyX = 1.0 + (profile.JellyAmplitudeX
                    * Math.Sin(Math.PI * 2.0 * profile.JellyFrequency * progress)
                    * Math.Exp(-profile.JellyFalloff * progress));
                double jellyY = 1.0 + (profile.JellyAmplitudeY
                    * Math.Sin((Math.PI * 2.0 * profile.JellyFrequency * progress) + 0.7)
                    * Math.Exp(-profile.JellyFalloff * progress));
                state.ScaleX = Scale(scale * jellyX);
                state.ScaleY = Scale(scale * jellyY);
                state.Opacity = FadeTail(eased);
                return state;
            }

            if (string.Equals(kind, PopOutKind.SpinOut, StringComparison.OrdinalIgnoreCase))
            {
                double radians = (profile.SpinTurns * Math.PI * 2.0) * eased;
                double degrees = radians * (180.0 / Math.PI);
                string axis = profile.SpinAxis ?? PopOutAxis.Y;
                if (string.Equals(axis, PopOutAxis.X, StringComparison.OrdinalIgnoreCase))
                {
                    state.ScaleY = Scale(Math.Abs(Math.Cos(radians)));
                }
                else if (string.Equals(axis, PopOutAxis.Z, StringComparison.OrdinalIgnoreCase))
                {
                    state.Rotation = degrees;
                }
                else
                {
                    state.ScaleX = Scale(Math.Abs(Math.Cos(radians)));
                }

                double endScale = 1.0 + ((profile.SpinEndScale - 1.0) * eased);
                state.ScaleX = Scale(state.ScaleX * endScale);
                state.ScaleY = Scale(state.ScaleY * endScale);
                state.Opacity = FadeTail(eased);
                return state;
            }

            // Dissolve and dust: the mask does the work, the glyph itself only fades at the very end.
            state.PieceClip = true;
            if (progress > 0.85)
            {
                state.Opacity = Math.Max(0.0, 1.0 - ((progress - 0.85) / 0.15));
            }

            return state;
        }

        /// <summary>Draws the pieces the wind carries away, in glyph space.</summary>
        public static void DrawParticles(
            DrawingContext drawingContext,
            PopOutPiecePlan plan,
            PopOutProfile profile,
            double progress,
            double fontSize,
            Brush? brush)
        {
            if (brush == null || plan.ParticleStart.Length == 0)
            {
                return;
            }

            if (!string.Equals(profile.Kind, PopOutKind.Dust, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            double wind = profile.WindAngleDegrees * (Math.PI / 180.0);
            double spread = Math.Max(0.0, profile.WindSpread) * (Math.PI / 180.0);
            double force = Math.Max(0.0, profile.WindForce) * fontSize;
            double baseSize = Math.Max(0.5, profile.ParticleSize * fontSize);
            double spinDegrees = profile.ParticleSpinDegrees;

            for (int index = 0; index < plan.ParticleStart.Length; index++)
            {
                double release = plan.ParticleOrder[index];
                if (progress <= release)
                {
                    continue;
                }

                double flight = (progress - release) / Math.Max(0.05, 1.0 - release);
                if (flight > 1.0)
                {
                    flight = 1.0;
                }

                double random = plan.ParticleRandom[index];
                double angle = wind + ((random - 0.5) * 2.0 * spread);
                double distance = force * (0.45 + (random * 0.9)) * flight;
                double x = plan.ParticleStart[index].X + (Math.Cos(angle) * distance);
                double y = plan.ParticleStart[index].Y + (Math.Sin(angle) * distance);
                double alpha = 1.0 - flight;
                if (alpha <= 0.02)
                {
                    continue;
                }

                double size = baseSize * (0.55 + (random * 0.9)) * (1.0 - (0.45 * flight));
                if (size < 0.4)
                {
                    continue;
                }

                double rotation = spinDegrees * ((random * 2.0) - 1.0) * flight;
                drawingContext.PushOpacity(alpha);
                bool turned = Math.Abs(rotation) > 0.05;
                if (turned)
                {
                    drawingContext.PushTransform(new RotateTransform(rotation, x, y));
                }

                drawingContext.DrawRectangle(brush, null, new Rect(x - (size * 0.5), y - (size * 0.5), size, size));
                if (turned)
                {
                    drawingContext.Pop();
                }

                drawingContext.Pop();
            }
        }

        /// <summary>
        /// Builds the grid of pieces for one glyph: cells in the glyph's own space, the order they
        /// leave in (origin direction blended with noise) and the particles that get blown away.
        /// </summary>
        private static PopOutPiecePlan BuildPlan(Rect bounds, PopOutProfile profile)
        {
            PopOutPiecePlan plan = new PopOutPiecePlan();
            if (bounds.IsEmpty || bounds.Width < 0.5 || bounds.Height < 0.5)
            {
                return plan;
            }

            plan.Bounds = bounds;
            int columns = Math.Clamp(profile.CellCount, 2, 40);
            double cellWidth = bounds.Width / columns;
            int rows = (int)Math.Round(bounds.Height / Math.Max(0.5, cellWidth));
            if (rows < 1)
            {
                rows = 1;
            }

            if (rows > 40)
            {
                rows = 40;
            }

            plan.Columns = columns;
            plan.Rows = rows;

            double width = bounds.Width / columns;
            double height = bounds.Height / rows;
            int count = columns * rows;
            Rect[] cells = new Rect[count];
            double[] order = new double[count];
            double noiseAmount = Math.Max(0.0, Math.Min(1.0, profile.NoiseAmount));
            int seed = profile.Seed;

            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    int index = (row * columns) + column;
                    cells[index] = new Rect(
                        bounds.X + (column * width),
                        bounds.Y + (row * height),
                        width,
                        height);

                    double nx = columns == 1 ? 0.5 : column / (double)(columns - 1);
                    double ny = rows == 1 ? 0.5 : row / (double)(rows - 1);
                    double sweep = Sweep(profile.Origin, nx, ny);
                    double noise = ValueNoise.Sample01(column * 0.55, row * 0.55, seed);
                    double value = (sweep * (1.0 - noiseAmount)) + (noise * noiseAmount);

                    // A little noise on top of a clean sweep, so a dissolve never looks like a wipe.
                    value += (ValueNoise.Sample01(column * 2.1, row * 2.1, seed + 977) - 0.5) * 0.12 * (1.0 - noiseAmount);
                    order[index] = value < 0.0 ? 0.0 : (value > 1.0 ? 1.0 : value);
                }
            }

            // Normalised so the crumble always uses the whole exit, whatever the origin and noise did.
            double min = double.MaxValue;
            double max = double.MinValue;
            for (int index = 0; index < count; index++)
            {
                if (order[index] < min)
                {
                    min = order[index];
                }

                if (order[index] > max)
                {
                    max = order[index];
                }
            }

            double range = max - min;
            if (range < 0.0001)
            {
                range = 1.0;
            }

            for (int index = 0; index < count; index++)
            {
                order[index] = ((order[index] - min) / range) * 0.985;
            }

            plan.Cells = cells;
            plan.CellOrder = order;

            int particleCount = Math.Clamp(profile.ParticleCount, 0, 64);
            Point[] starts = new Point[particleCount];
            double[] particleOrder = new double[particleCount];
            double[] particleRandom = new double[particleCount];
            for (int index = 0; index < particleCount; index++)
            {
                double px = ValueNoise.Hash01(index, 11, seed);
                double py = ValueNoise.Hash01(index, 29, seed);
                starts[index] = new Point(bounds.X + (px * bounds.Width), bounds.Y + (py * bounds.Height));

                int cellColumn = Math.Min(columns - 1, (int)(px * columns));
                int cellRow = Math.Min(rows - 1, (int)(py * rows));
                particleOrder[index] = order[(cellRow * columns) + cellColumn];
                particleRandom[index] = ValueNoise.Hash01(index, 53, seed);
            }

            plan.ParticleStart = starts;
            plan.ParticleOrder = particleOrder;
            plan.ParticleRandom = particleRandom;
            return plan;
        }

        /// <summary>0 at the edge the crumble starts on, 1 at the far side, using the gradient names.</summary>
        private static double Sweep(string? origin, double nx, double ny)
        {
            string value = origin ?? PopOutOrigin.LeftToRight;
            if (string.Equals(value, PopOutOrigin.RightToLeft, StringComparison.OrdinalIgnoreCase))
            {
                return 1.0 - nx;
            }

            if (string.Equals(value, PopOutOrigin.TopToBottom, StringComparison.OrdinalIgnoreCase))
            {
                return ny;
            }

            if (string.Equals(value, PopOutOrigin.BottomToTop, StringComparison.OrdinalIgnoreCase))
            {
                return 1.0 - ny;
            }

            if (string.Equals(value, PopOutOrigin.DownRight, StringComparison.OrdinalIgnoreCase))
            {
                return (nx + ny) * 0.5;
            }

            if (string.Equals(value, PopOutOrigin.UpRight, StringComparison.OrdinalIgnoreCase))
            {
                return (nx + (1.0 - ny)) * 0.5;
            }

            if (string.Equals(value, PopOutOrigin.DownLeft, StringComparison.OrdinalIgnoreCase))
            {
                return ((1.0 - nx) + ny) * 0.5;
            }

            if (string.Equals(value, PopOutOrigin.UpLeft, StringComparison.OrdinalIgnoreCase))
            {
                return ((1.0 - nx) + (1.0 - ny)) * 0.5;
            }

            if (string.Equals(value, PopOutOrigin.Center, StringComparison.OrdinalIgnoreCase))
            {
                double dx = (nx * 2.0) - 1.0;
                double dy = (ny * 2.0) - 1.0;
                double distance = Math.Min(1.0, Math.Sqrt((dx * dx) + (dy * dy)) / 1.4142);
                return 1.0 - distance;
            }

            if (string.Equals(value, PopOutOrigin.Random, StringComparison.OrdinalIgnoreCase))
            {
                return 0.5;
            }

            return nx;
        }

        internal static double Ease(double progress, double power)
        {
            double clamped = progress <= 0.0 ? 0.0 : (progress >= 1.0 ? 1.0 : progress);
            double exponent = power <= 0.05 ? 1.0 : power;
            return Math.Pow(clamped, exponent);
        }

        /// <summary>Full opacity until the last third, then a fade so nothing pops out of existence.</summary>
        private static double FadeTail(double eased)
        {
            if (eased <= 0.7)
            {
                return 1.0;
            }

            return Math.Max(0.0, 1.0 - ((eased - 0.7) / 0.3));
        }

        /// <summary>A scale of exactly zero makes a degenerate matrix; a sliver renders the same and is safe.</summary>
        private static double Scale(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 1.0;
            }

            double magnitude = Math.Abs(value);
            if (magnitude < 0.004)
            {
                return value < 0.0 ? -0.004 : 0.004;
            }

            return value;
        }

        private static void ResolveDirection(string? direction, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;
            string value = direction ?? PopInDirection.Bottom;
            if (string.Equals(value, PopInDirection.Top, StringComparison.OrdinalIgnoreCase))
            {
                y = -1.0;
                return;
            }

            if (string.Equals(value, PopInDirection.Left, StringComparison.OrdinalIgnoreCase))
            {
                x = -1.0;
                return;
            }

            if (string.Equals(value, PopInDirection.Right, StringComparison.OrdinalIgnoreCase))
            {
                x = 1.0;
                return;
            }

            y = 1.0;
        }
    }
}
