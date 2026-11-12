using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SubtitleForge.Models;
using SubtitleForge.Services;

namespace SubtitleForge.Controls
{
    /// <summary>
    /// Renders the subtitle text as per-character geometry so every glyph can be animated on
    /// its own: fill (solid or gradient), any number of outlines and shadows applied either to
    /// the glyphs or to the finished composition, plus idle motion and a pop-in entrance.
    /// Font sizes in the style profile are pixels on a 1080-tall canvas; the control scales
    /// them to its own height so the preview and a full-screen stream overlay look identical.
    /// </summary>
    public sealed class SubtitleOverlay : FrameworkElement
    {
        /// <summary>Height of the design canvas the profile font sizes are authored against.</summary>
        public const double DesignHeight = 1080.0;
        private const double DefaultCharacterStagger = 0.06;

        private readonly List<GlyphBox> glyphs = new List<GlyphBox>();

        private string layoutKey = string.Empty;
        private Rect textBounds = Rect.Empty;
        private int activeWordIndex = -1;
        private int highlightWordIndex = -1;
        private int renderedWordIndex = -1;
        private double layoutFontSize;

        private sealed class GlyphBox
        {
            public Geometry Geometry = Geometry.Empty;
            public Rect Bounds = Rect.Empty;
            public int WordIndex;
            public int IndexInWord;

            /// <summary>Glyph index inside the cue, spaces excluded.</summary>
            public int Index;
            public double AppearSeconds;

            /// <summary>Transform worked out in the measuring pass, reused by the draw pass.</summary>
            public Matrix DrawMatrix = Matrix.Identity;

            public double DrawOpacity = 1.0;

            /// <summary>False when the glyph is hidden or not due yet.</summary>
            public bool Drawn;
        }

        public StyleProfile? TextStyle { get; set; }

        public IdleAnimationProfile? Idle { get; set; }

        public PopInProfile? PopIn { get; set; }

        public string CueText { get; set; } = string.Empty;

        public double CueStartSeconds { get; set; }

        public double CueEndSeconds { get; set; }

        public double PlayheadSeconds { get; set; }

        /// <summary>Free-running clock for the idle animation, so it keeps moving while paused.</summary>
        public double IdleSeconds { get; set; }

        /// <summary>Horizontal anchor of the text centre, 0-1 of the control width.</summary>
        public double PositionX { get; set; } = 0.5;

        /// <summary>Vertical anchor of the text centre, 0-1 of the control height.</summary>
        public double PositionY { get; set; } = 0.86;

        public double Scale { get; set; } = 1.0;

        public bool HasContent
        {
            get { return !string.IsNullOrEmpty(CueText) && TextStyle != null; }
        }

        public void Refresh()
        {
            InvalidateVisual();
        }

        /// <summary>
        /// True when something in the current state changes every frame, so the host knows it has
        /// to keep repainting. A static cue on a static style returns false and costs nothing.
        /// </summary>
        public bool IsAnimating
        {
            get
            {
                if (!HasContent)
                {
                    return false;
                }

                IdleAnimationProfile? idle = Idle;
                if (idle != null && idle.HasAnyChannel)
                {
                    return true;
                }

                PopInProfile? popIn = PopIn;
                if (popIn == null || string.Equals(popIn.Kind, PopInKind.None, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                double elapsed = PlayheadSeconds - CueStartSeconds;
                double span = Math.Max(0.01, popIn.DurationSeconds);
                if (popIn.PerCharacter && !string.Equals(popIn.StaggerGrouping, StaggerGroupings.Cue, StringComparison.OrdinalIgnoreCase))
                {
                    span += Math.Max(0.0, popIn.CharacterDelaySeconds) * Math.Max(1, (CueText ?? string.Empty).Length);
                }

                return elapsed < span + 0.1;
            }
        }

        /// <summary>Word index the last paint highlighted, used to detect word-boundary changes.</summary>
        public int RenderedWordIndex
        {
            get { return renderedWordIndex; }
        }

        /// <summary>The word index the playhead is inside right now.</summary>
        public int CurrentWordIndex
        {
            get
            {
                StyleProfile? style = TextStyle;
                if (style == null || !style.HighlightSpokenWord && style.WordsPerDisplay <= 0)
                {
                    return -1;
                }

                List<WordTiming> words = SubtitleTextPlan.BuildWords(
                    CueText ?? string.Empty,
                    CueStartSeconds,
                    Math.Max(0.01, CueEndSeconds - CueStartSeconds));
                return SubtitleTextPlan.FindWordIndexAt(words, PlayheadSeconds);
            }
        }

        /// <summary>Drops the cached glyph layout (call when the style or the text changes).</summary>
        public void InvalidateLayout()
        {
            layoutKey = string.Empty;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? 0.0 : availableSize.Width;
            double height = double.IsInfinity(availableSize.Height) ? 0.0 : availableSize.Height;
            return new Size(width, height);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            PaintFrame(drawingContext, ActualWidth, ActualHeight);
        }

        /// <summary>
        /// Paints one frame into a canvas of an explicit size. The burn-in renderer calls this on its
        /// own drawing visual, so an exported frame never depends on WPF having laid this control
        /// out — a detached visual has no RenderSize, and the old code silently drew nothing
        /// because of that.
        /// </summary>
        public void PaintFrame(DrawingContext drawingContext, double canvasWidth, double canvasHeight)
        {
            StyleProfile? style = TextStyle;
            if (style == null || canvasHeight <= 1.0 || canvasWidth <= 1.0)
            {
                return;
            }

            double canvasScale = canvasHeight / DesignHeight;
            double fontSize = style.FontSize * canvasScale * Math.Max(0.05, Scale);
            if (fontSize < 4.0)
            {
                return;
            }

            EnsureLayout(style, fontSize);
            if (glyphs.Count == 0)
            {
                return;
            }

            double originX = (PositionX * canvasWidth) - (textBounds.Width * 0.5);
            double originY = (PositionY * canvasHeight) - (textBounds.Height * 0.5);

            double globalOpacity = style.Opacity < 0.0 ? 0.0 : (style.Opacity > 1.0 ? 1.0 : style.Opacity);
            if (globalOpacity <= 0.001)
            {
                return;
            }

            // Brushes are built once per frame and reused by every glyph; per-glyph fading is
            // applied with PushOpacity so nothing has to be allocated inside the loop.
            IdleAnimationProfile? idle = Idle;
            PopInProfile? popIn = PopIn;
            double popDuration = popIn == null ? 0.0 : Math.Max(0.01, popIn.DurationSeconds);
            double transformScale = canvasScale * Math.Max(0.05, Scale);

            // Gradients are resolved in the same space the geometry is drawn in (the glyph space,
            // before the origin translate), otherwise a gradient anchored at the viewport position
            // of the text only shows a sliver of its ramp and looks like a flat colour.
            PaintSet paint = BuildPaintSet(style, textBounds, transformScale);

            drawingContext.PushOpacity(globalOpacity);
            drawingContext.PushTransform(new TranslateTransform(originX, originY));

            // Two passes: the effects that wrap the whole block are painted first, around one
            // silhouette of every glyph, then each glyph gets its own effects and fill. The first
            // pass therefore only works out the transforms.
            GeometryGroup? composition = null;
            if (paint.HasCompositionEffect)
            {
                composition = new GeometryGroup();
                composition.FillRule = FillRule.Nonzero;
            }

            for (int index = 0; index < glyphs.Count; index++)
            {
                GlyphBox glyph = glyphs[index];
                glyph.Drawn = false;
                if (glyph.Geometry.IsEmpty())
                {
                    continue;
                }

                double elapsed = PlayheadSeconds - glyph.AppearSeconds;
                if (elapsed < 0.0)
                {
                    continue;
                }

                double popProgress = 1.0;
                if (popIn != null && !string.Equals(popIn.Kind, PopInKind.None, StringComparison.OrdinalIgnoreCase))
                {
                    popProgress = elapsed / popDuration;
                    if (popProgress > 1.0)
                    {
                        popProgress = 1.0;
                    }
                }

                double opacity = 1.0;
                double slideX = 0.0;
                double slideY = 0.0;
                double popScaleX = 1.0;
                double popScaleY = 1.0;

                if (popIn != null)
                {
                    string kind = popIn.Kind ?? PopInKind.None;

                    if (string.Equals(kind, PopInKind.SlideIn, StringComparison.OrdinalIgnoreCase))
                    {
                        double spring = Spring(popProgress, popIn.SpringStiffness, popIn.SpringDamping);
                        double distance = popIn.DistanceMultiplier * fontSize;
                        double remaining = (1.0 - spring) * distance;
                        ResolveSlideAxis(popIn.Direction, out double dirX, out double dirY);
                        slideX = dirX * remaining;
                        slideY = dirY * remaining;
                        opacity = Math.Min(1.0, popProgress * 3.0);
                    }
                    else if (string.Equals(kind, PopInKind.ScaleIn, StringComparison.OrdinalIgnoreCase))
                    {
                        double spring = Spring(popProgress, 10.0, 0.5);
                        double jellyX = 1.0 + (popIn.JellyAmplitudeX
                            * Math.Sin(Math.PI * 2.0 * popIn.JellyFrequency * popProgress)
                            * Math.Exp(-popIn.JellyFalloff * popProgress));
                        double jellyY = 1.0 + (popIn.JellyAmplitudeY
                            * Math.Sin((Math.PI * 2.0 * popIn.JellyFrequency * popProgress) + 0.7)
                            * Math.Exp(-popIn.JellyFalloff * popProgress));
                        popScaleX = spring * jellyX;
                        popScaleY = spring * jellyY;
                        opacity = Math.Min(1.0, popProgress * 4.0);
                    }
                    else if (string.Equals(kind, PopInKind.FadeIn, StringComparison.OrdinalIgnoreCase))
                    {
                        opacity = popProgress;
                    }
                }

                double offsetX = slideX;
                double offsetY = slideY;
                double scaleX = popScaleX;
                double scaleY = popScaleY;
                double rotation = 0.0;

                if (idle != null && idle.HasAnyChannel)
                {
                    string idleGrouping = string.IsNullOrEmpty(idle.StaggerGrouping)
                        ? StaggerGroupings.Character
                        : idle.StaggerGrouping;
                    double staggerIndex = 0.0;
                    if (idle.PerCharacter && !string.Equals(idleGrouping, StaggerGroupings.Cue, StringComparison.OrdinalIgnoreCase))
                    {
                        staggerIndex = string.Equals(idleGrouping, StaggerGroupings.Word, StringComparison.OrdinalIgnoreCase)
                            ? glyph.WordIndex
                            : glyph.Index;
                    }

                    double idleTime = IdleSeconds - (staggerIndex * Math.Max(0.0, idle.CharacterStaggerSeconds));

                    double tau = Math.PI * 2.0;

                    if (idle.Shake.Enabled)
                    {
                        double frequency = Math.Max(0.05, idle.Shake.Frequency);
                        double amplitude = idle.Shake.Amplitude * fontSize;
                        offsetX += amplitude * 0.5 * (
                            Math.Sin((tau * frequency * idleTime * 7.0) + Phase(idle.Shake.Phase))
                            + Math.Sin(tau * frequency * idleTime * 11.7));
                        offsetY += amplitude * 0.5 * (
                            Math.Cos((tau * frequency * idleTime * 8.3) + Phase(idle.Shake.Phase))
                            + Math.Sin(tau * frequency * idleTime * 13.1));
                    }

                    if (idle.SineX.Enabled)
                    {
                        offsetX += idle.SineX.Amplitude * fontSize
                            * Math.Sin((tau * Math.Max(0.01, idle.SineX.Frequency) * idleTime) + Phase(idle.SineX.Phase));
                    }

                    if (idle.SineY.Enabled)
                    {
                        offsetY += idle.SineY.Amplitude * fontSize
                            * Math.Sin((tau * Math.Max(0.01, idle.SineY.Frequency) * idleTime) + Phase(idle.SineY.Phase));
                    }

                    if (idle.Pulse.Enabled)
                    {
                        double pulse = 1.0 + (idle.Pulse.Amplitude
                            * Math.Sin((tau * Math.Max(0.01, idle.Pulse.Frequency) * idleTime) + Phase(idle.Pulse.Phase)));
                        if (pulse < 0.05)
                        {
                            pulse = 0.05;
                        }

                        scaleX *= pulse;
                        scaleY *= pulse;
                    }

                    if (idle.Wiggle.Enabled)
                    {
                        rotation += idle.Wiggle.Amplitude
                            * Math.Sin((tau * Math.Max(0.01, idle.Wiggle.Frequency) * idleTime) + Phase(idle.Wiggle.Phase));
                    }

                    if (idle.Spin.Enabled)
                    {
                        double angle = (tau * Math.Max(0.01, idle.Spin.Frequency) * idleTime) + Phase(idle.Spin.Phase);
                        double squash = Math.Abs(Math.Cos(angle));
                        if (squash < 0.06)
                        {
                            squash = 0.06;
                        }

                        scaleX *= squash;
                    }
                }

                if (opacity <= 0.001)
                {
                    continue;
                }

                double centerX = glyph.Bounds.X + (glyph.Bounds.Width * 0.5);
                double centerY = glyph.Bounds.Y + (glyph.Bounds.Height * 0.5);

                bool scaled = Math.Abs(scaleX - 1.0) > 0.0001 || Math.Abs(scaleY - 1.0) > 0.0001;
                bool rotated = Math.Abs(rotation) > 0.0001;
                bool moved = Math.Abs(offsetX) > 0.0001 || Math.Abs(offsetY) > 0.0001;

                // A TransformGroup gives exactly the matrix the three nested pushes used to produce,
                // and the same matrix can be baked into the composition silhouette.
                Matrix matrix = Matrix.Identity;
                if (scaled || rotated || moved)
                {
                    TransformGroup group = new TransformGroup();
                    if (scaled)
                    {
                        group.Children.Add(new ScaleTransform(scaleX, scaleY, centerX, centerY));
                    }

                    if (rotated)
                    {
                        group.Children.Add(new RotateTransform(rotation, centerX, centerY));
                    }

                    if (moved)
                    {
                        group.Children.Add(new TranslateTransform(offsetX, offsetY));
                    }

                    matrix = group.Value;
                }

                glyph.DrawMatrix = matrix;
                glyph.DrawOpacity = opacity;
                glyph.Drawn = true;

                if (composition != null)
                {
                    composition.Children.Add(matrix.IsIdentity
                        ? glyph.Geometry
                        : TransformedCopy(glyph.Geometry, matrix));
                }
            }

            if (composition != null && composition.Children.Count > 0)
            {
                DrawEffects(drawingContext, composition, style, paint, EffectTarget.Composition);
            }

            for (int index = 0; index < glyphs.Count; index++)
            {
                GlyphBox glyph = glyphs[index];
                if (!glyph.Drawn)
                {
                    continue;
                }

                drawingContext.PushOpacity(glyph.DrawOpacity);
                if (!glyph.DrawMatrix.IsIdentity)
                {
                    drawingContext.PushTransform(new MatrixTransform(glyph.DrawMatrix));
                }

                DrawEffects(drawingContext, glyph.Geometry, style, paint, EffectTarget.Font);

                bool highlighted = style.HighlightSpokenWord && glyph.WordIndex == highlightWordIndex;
                Brush? brush = highlighted && paint.Highlight != null ? paint.Highlight : paint.Fill;
                if (brush != null)
                {
                    drawingContext.DrawGeometry(brush, null, glyph.Geometry);
                }

                if (!glyph.DrawMatrix.IsIdentity)
                {
                    drawingContext.Pop();
                }

                drawingContext.Pop();
            }

            drawingContext.Pop();
            drawingContext.Pop();
        }

        private sealed class PaintSet
        {
            public Brush? Fill;
            public Brush? Highlight;

            // One slot per effect layer. Brushes and pens are built once per frame: creating them per
            // glyph per layer dominated the frame time on long cues.
            public Brush?[] EffectBrush = Array.Empty<Brush?>();
            public Pen?[] EffectPen = Array.Empty<Pen?>();
            public Pen?[] EffectHaloPen = Array.Empty<Pen?>();
            public double[] EffectOffsetX = Array.Empty<double>();
            public double[] EffectOffsetY = Array.Empty<double>();
            public bool[] EffectIsShadow = Array.Empty<bool>();
            public bool[] EffectInside = Array.Empty<bool>();
            public bool[] EffectFilled = Array.Empty<bool>();
            public string[] EffectTarget = Array.Empty<string>();

            /// <summary>True when any enabled effect wraps the whole composition instead of each glyph.</summary>
            public bool HasCompositionEffect;
        }

        private static Pen CreatePen(Brush brush, double thickness)
        {
            Pen pen = new Pen(brush, thickness);
            pen.LineJoin = PenLineJoin.Round;
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
            pen.Freeze();
            return pen;
        }

        private static PaintSet BuildPaintSet(StyleProfile style, Rect bounds, double scaleFactor)
        {
            PaintSet paint = new PaintSet();
            paint.Fill = StyleBrushes.CreateBrush(style.Fill, bounds, 1.0);
            paint.Highlight = style.HighlightSpokenWord
                ? StyleBrushes.CreateBrush(style.HighlightFill, bounds, 1.0)
                : null;

            int count = style.Effects.Count;
            paint.EffectBrush = new Brush?[count];
            paint.EffectPen = new Pen?[count];
            paint.EffectHaloPen = new Pen?[count];
            paint.EffectOffsetX = new double[count];
            paint.EffectOffsetY = new double[count];
            paint.EffectIsShadow = new bool[count];
            paint.EffectInside = new bool[count];
            paint.EffectFilled = new bool[count];
            paint.EffectTarget = new string[count];

            for (int index = 0; index < count; index++)
            {
                EffectLayer effect = style.Effects[index];
                paint.EffectTarget[index] = effect.Target;
                paint.EffectIsShadow[index] = effect.IsShadow;
                paint.EffectOffsetX[index] = effect.OffsetX * scaleFactor;
                paint.EffectOffsetY[index] = effect.OffsetY * scaleFactor;

                if (!effect.Enabled)
                {
                    continue;
                }

                if (string.Equals(effect.Target, EffectTarget.Composition, StringComparison.OrdinalIgnoreCase))
                {
                    paint.HasCompositionEffect = true;
                }

                if (effect.IsShadow)
                {
                    double layerOpacity = Math.Max(0.0, Math.Min(1.0, effect.Opacity));
                    Brush? core = StyleBrushes.CreateBrush(effect.Color, bounds, layerOpacity);
                    paint.EffectBrush[index] = core;

                    Brush? soft = StyleBrushes.CreateBrush(effect.Color, bounds, layerOpacity * 0.45);
                    double halo = Math.Abs(effect.Thickness) * scaleFactor * 2.0;
                    if (soft != null && halo > 0.6)
                    {
                        paint.EffectHaloPen[index] = CreatePen(soft, halo);
                    }
                }
                else
                {
                    Brush? brush = StyleBrushes.CreateBrush(effect.Color, bounds, 1.0);
                    paint.EffectBrush[index] = brush;
                    paint.EffectFilled[index] = effect.Filled;

                    // A negative thickness is the shorthand for an inside outline; the pen keeps its
                    // full width and the glyph is clipped to its own geometry, which leaves half of
                    // the stroke visible on the inside.
                    paint.EffectInside[index] = effect.Inside || effect.Thickness < 0.0;

                    double width = Math.Abs(effect.Thickness) * scaleFactor * 2.0;
                    if (brush != null && width > 0.2)
                    {
                        paint.EffectPen[index] = CreatePen(brush, width);
                    }
                }
            }

            return paint;
        }

        /// <summary>
        /// Copies a glyph outline with its transform baked in, so several glyphs can be added to one
        /// silhouette. WPF has no "get transformed copy" on <see cref="Geometry"/>, but a path keeps
        /// its own <see cref="Geometry.Transform"/> and a <see cref="GeometryGroup"/> honours it.
        /// </summary>
        private static Geometry TransformedCopy(Geometry geometry, Matrix matrix)
        {
            PathGeometry path = PathGeometry.CreateFromGeometry(geometry);
            path.Transform = new MatrixTransform(matrix);
            return path;
        }

        /// <summary>
        /// Paints the effect stack in list order, skipping entries that belong to the other target
        /// group. Shadows paint their soft halo first so the core sits on top of it.
        /// </summary>
        private void DrawEffects(
            DrawingContext drawingContext,
            Geometry geometry,
            StyleProfile style,
            PaintSet paint,
            string target)
        {
            int count = Math.Min(paint.EffectBrush.Length, style.Effects.Count);
            for (int index = 0; index < count; index++)
            {
                EffectLayer effect = style.Effects[index];
                if (!effect.Enabled || !MatchesTarget(paint.EffectTarget[index], target))
                {
                    continue;
                }

                Brush? brush = paint.EffectBrush[index];
                Pen? pen = paint.EffectPen[index];
                Pen? halo = paint.EffectHaloPen[index];
                if (brush == null && pen == null && halo == null)
                {
                    continue;
                }

                double offsetX = paint.EffectOffsetX[index];
                double offsetY = paint.EffectOffsetY[index];
                bool shifted = Math.Abs(offsetX) > 0.001 || Math.Abs(offsetY) > 0.001;

                if (shifted)
                {
                    drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
                }

                if (paint.EffectIsShadow[index])
                {
                    if (halo != null)
                    {
                        drawingContext.DrawGeometry(null, halo, geometry);
                    }

                    if (brush != null)
                    {
                        drawingContext.DrawGeometry(brush, null, geometry);
                    }
                }
                else
                {
                    bool inside = paint.EffectInside[index];
                    Brush? fill = paint.EffectFilled[index] ? brush : null;

                    if (inside)
                    {
                        drawingContext.PushClip(geometry);
                    }

                    drawingContext.DrawGeometry(fill, pen, geometry);

                    if (inside)
                    {
                        drawingContext.Pop();
                    }
                }

                if (shifted)
                {
                    drawingContext.Pop();
                }
            }
        }

        private static bool MatchesTarget(string? value, string expected)
        {
            string actual = string.IsNullOrEmpty(value) ? EffectTarget.Font : value;
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private void EnsureLayout(StyleProfile style, double fontSize)
        {
            string text = CueText ?? string.Empty;
            List<WordTiming> words = SubtitleTextPlan.BuildWords(
                text,
                CueStartSeconds,
                Math.Max(0.01, CueEndSeconds - CueStartSeconds));

            activeWordIndex = SubtitleTextPlan.FindWordIndexAt(words, PlayheadSeconds);
            renderedWordIndex = activeWordIndex;
            List<WordTiming> visible = SubtitleTextPlan.WindowWords(words, activeWordIndex, style.WordsPerDisplay);
            string display = SubtitleTextPlan.JoinWords(visible);

            // The window shifts the word indices, so the highlight has to be expressed in the same
            // (window-local) space the glyphs use.
            int windowOffset = 0;
            if (words.Count > 0 && visible.Count > 0 && visible.Count < words.Count)
            {
                int offset = words.IndexOf(visible[0]);
                if (offset > 0)
                {
                    windowOffset = offset;
                }
            }

            highlightWordIndex = activeWordIndex < 0 ? -1 : activeWordIndex - windowOffset;

            PopInProfile? popIn = PopIn;
            bool enterAnimation = popIn != null
                && !string.Equals(popIn.Kind, PopInKind.None, StringComparison.OrdinalIgnoreCase);
            string enterGrouping = popIn == null
                ? StaggerGroupings.Cue
                : (string.IsNullOrEmpty(popIn.StaggerGrouping) ? StaggerGroupings.Character : popIn.StaggerGrouping);
            bool perCharacterEnter = enterAnimation
                && popIn!.PerCharacter
                && !string.Equals(enterGrouping, StaggerGroupings.Cue, StringComparison.OrdinalIgnoreCase);
            bool perWordEnter = perCharacterEnter
                && string.Equals(enterGrouping, StaggerGroupings.Word, StringComparison.OrdinalIgnoreCase);
            double characterDelay = popIn == null ? 0.0 : Math.Max(0.0, popIn.CharacterDelaySeconds);

            string key = string.Concat(
                display,
                "|",
                fontSize.ToString("0.##", CultureInfo.InvariantCulture),
                "|",
                style.FontFamily,
                "|",
                style.Bold ? "b" : "-",
                style.Italic ? "i" : "-",
                "|",
                perCharacterEnter ? (perWordEnter ? "w" : "c") : "0",
                "|",
                characterDelay.ToString("0.###", CultureInfo.InvariantCulture),
                "|",
                CueStartSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "|",
                activeWordIndex.ToString(CultureInfo.InvariantCulture),
                "|",
                style.WordsPerDisplay.ToString(CultureInfo.InvariantCulture),
                "|",
                style.CharacterSpacing.ToString("0.###", CultureInfo.InvariantCulture),
                "|",
                style.WordSpacing.ToString("0.###", CultureInfo.InvariantCulture));

            if (string.Equals(key, layoutKey, StringComparison.Ordinal))
            {
                return;
            }

            layoutKey = key;
            layoutFontSize = fontSize;
            glyphs.Clear();
            textBounds = Rect.Empty;

            if (string.IsNullOrEmpty(display) || visible.Count == 0)
            {
                return;
            }

            Typeface typeface = new Typeface(
                new FontFamily(string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily),
                style.Italic ? FontStyles.Italic : FontStyles.Normal,
                style.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);

            double pixelsPerDip = 1.0;
            try
            {
                pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            }
            catch (Exception)
            {
                pixelsPerDip = 1.0;
            }

            // Spacing is authored in design pixels, so it has to follow the same scale as the font.
            double spacingScale = style.FontSize > 0.001 ? fontSize / style.FontSize : 1.0;
            double characterSpacing = style.CharacterSpacing * spacingScale;
            double wordSpacing = style.WordSpacing * spacingScale;

            double cursorX = 0.0;
            double lineHeight = fontSize * 1.35;

            // A per-character entrance runs through the whole cue as one continuous sequence, spaces
            // included, so the letters arrive in reading order instead of restarting at every word.
            int staggerIndex = 0;
            int glyphIndex = 0;
            Rect union = Rect.Empty;

            for (int wordIndex = 0; wordIndex < visible.Count; wordIndex++)
            {
                WordTiming word = visible[wordIndex];
                int wordPosition = 0;

                for (int charIndex = 0; charIndex < word.Text.Length; charIndex++)
                {
                    string symbol = word.Text.Substring(charIndex, 1);
                    FormattedText formatted = new FormattedText(
                        symbol,
                        CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        Brushes.White,
                        pixelsPerDip);

                    if (formatted.Height > lineHeight)
                    {
                        lineHeight = formatted.Height;
                    }

                    double advance = formatted.WidthIncludingTrailingWhitespace;

                    Geometry geometry = formatted.BuildGeometry(new Point(cursorX, 0.0));
                    if (!geometry.IsEmpty())
                    {
                        Rect bounds = geometry.Bounds;
                        if (bounds.Width > 0.01 || bounds.Height > 0.01)
                        {
                            GlyphBox box = new GlyphBox();
                            box.Geometry = geometry;
                            box.Bounds = bounds;
                            box.WordIndex = wordIndex;
                            box.IndexInWord = wordPosition;
                            box.Index = glyphIndex;
                            double appearBase = style.WordsPerDisplay > 0
                                ? word.StartSeconds
                                : CueStartSeconds;
                            double enterStagger = 0.0;
                            if (perWordEnter)
                            {
                                enterStagger = wordIndex * characterDelay;
                            }
                            else if (perCharacterEnter)
                            {
                                enterStagger = staggerIndex * characterDelay;
                            }

                            box.AppearSeconds = appearBase + enterStagger;
                            glyphs.Add(box);
                            union = union.IsEmpty ? bounds : Rect.Union(union, bounds);
                        }
                    }

                    if (!char.IsWhiteSpace(symbol[0]))
                    {
                        wordPosition++;
                    }

                    cursorX += advance + characterSpacing;
                    staggerIndex++;
                    glyphIndex++;
                }

                // Space between words.
                if (wordIndex < visible.Count - 1)
                {
                    FormattedText space = new FormattedText(
                        " ",
                        CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        Brushes.White,
                        pixelsPerDip);
                    cursorX += space.WidthIncludingTrailingWhitespace + characterSpacing + wordSpacing;
                    staggerIndex++;
                }
            }

            if (union.IsEmpty)
            {
                glyphs.Clear();
                return;
            }

            textBounds = new Rect(0.0, union.Y, cursorX, Math.Max(union.Height, lineHeight));
        }

        private static double Phase(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        /// <summary>Damped spring, 0 at t=0, overshoots and settles at ~1 when t reaches 1.</summary>
        public static double Spring(double t, double stiffness, double damping)
        {
            double clamped = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
            if (clamped <= 0.0)
            {
                return 0.0;
            }

            if (clamped >= 1.0)
            {
                return 1.0;
            }

            double omega = Math.Max(0.5, stiffness);
            double decay = Math.Max(0.05, damping);
            double value = 1.0 - (Math.Exp(-decay * omega * clamped) * Math.Cos(omega * clamped));
            return value;
        }

        private static void ResolveSlideAxis(string? direction, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;

            if (string.Equals(direction, PopInDirection.Top, StringComparison.OrdinalIgnoreCase))
            {
                y = -1.0;
                return;
            }

            if (string.Equals(direction, PopInDirection.Left, StringComparison.OrdinalIgnoreCase))
            {
                x = -1.0;
                return;
            }

            if (string.Equals(direction, PopInDirection.Right, StringComparison.OrdinalIgnoreCase))
            {
                x = 1.0;
                return;
            }

            y = 1.0;
        }

        public double LayoutFontSize
        {
            get { return layoutFontSize; }
        }
    }
}
