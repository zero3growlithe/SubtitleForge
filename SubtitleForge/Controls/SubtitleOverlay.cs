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
        private const double DesignHeight = 1080.0;
        private const double DefaultCharacterStagger = 0.06;

        private readonly List<GlyphBox> glyphs = new List<GlyphBox>();

        private string layoutKey = string.Empty;
        private Rect textBounds = Rect.Empty;
        private int activeWordIndex = -1;
        private double layoutFontSize;

        private sealed class GlyphBox
        {
            public Geometry Geometry = Geometry.Empty;
            public Rect Bounds = Rect.Empty;
            public int WordIndex;
            public int IndexInWord;
            public int Index;
            public double AppearSeconds;
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

            StyleProfile? style = TextStyle;
            if (style == null || ActualHeight <= 1.0 || ActualWidth <= 1.0)
            {
                return;
            }

            double canvasScale = ActualHeight / DesignHeight;
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

            double originX = (PositionX * ActualWidth) - (textBounds.Width * 0.5);
            double originY = (PositionY * ActualHeight) - (textBounds.Height * 0.5);

            Rect absoluteBounds = new Rect(
                originX + textBounds.X,
                originY + textBounds.Y,
                textBounds.Width,
                textBounds.Height);

            double globalOpacity = style.Opacity < 0.0 ? 0.0 : (style.Opacity > 1.0 ? 1.0 : style.Opacity);
            Brush? fill = StyleBrushes.CreateBrush(style.Fill, absoluteBounds, globalOpacity);
            Brush? highlight = style.HighlightSpokenWord
                ? StyleBrushes.CreateBrush(style.HighlightFill, absoluteBounds, globalOpacity)
                : fill;

            IdleAnimationProfile? idle = Idle;
            PopInProfile? popIn = PopIn;
            double popDuration = popIn == null ? 0.0 : Math.Max(0.01, popIn.DurationSeconds);

            for (int index = 0; index < glyphs.Count; index++)
            {
                GlyphBox glyph = glyphs[index];
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
                        double jelly = 1.0 + (popIn.JellyAmplitudeX
                            * Math.Sin(Math.PI * 2.0 * popIn.JellyFrequency * popProgress)
                            * Math.Exp(-popIn.JellyFalloff * popProgress));
                        double jellyY = 1.0 + (popIn.JellyAmplitudeY
                            * Math.Sin((Math.PI * 2.0 * popIn.JellyFrequency * popProgress) + 0.7)
                            * Math.Exp(-popIn.JellyFalloff * popProgress));
                        popScaleX = spring * jelly;
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
                    double idleTime = IdleSeconds - (idle.PerCharacter
                        ? glyph.Index * Math.Max(0.0, idle.CharacterStaggerSeconds)
                        : 0.0);

                    double tau = Math.PI * 2.0;

                    if (idle.Shake.Enabled)
                    {
                        double frequency = Math.Max(0.05, idle.Shake.Frequency);
                        double amplitude = idle.Shake.Amplitude * fontSize;
                        offsetX += amplitude * 0.5 * (
                            Math.Sin(tau * frequency * idleTime * 7.0 + Phase(idle.Shake.Phase))
                            + Math.Sin(tau * frequency * idleTime * 11.7));
                        offsetY += amplitude * 0.5 * (
                            Math.Cos(tau * frequency * idleTime * 8.3 + Phase(idle.Shake.Phase))
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
                        double angle = tau * Math.Max(0.01, idle.Spin.Frequency) * idleTime + Phase(idle.Spin.Phase);
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

                double centerX = originX + glyph.Bounds.X + (glyph.Bounds.Width * 0.5);
                double centerY = originY + glyph.Bounds.Y + (glyph.Bounds.Height * 0.5);

                TransformGroup transform = new TransformGroup();
                transform.Children.Add(new TranslateTransform(originX, originY));
                if (Math.Abs(scaleX - 1.0) > 0.0001 || Math.Abs(scaleY - 1.0) > 0.0001)
                {
                    transform.Children.Add(new ScaleTransform(scaleX, scaleY, centerX, centerY));
                }

                if (Math.Abs(rotation) > 0.0001)
                {
                    transform.Children.Add(new RotateTransform(rotation, centerX, centerY));
                }

                if (Math.Abs(offsetX) > 0.0001 || Math.Abs(offsetY) > 0.0001)
                {
                    transform.Children.Add(new TranslateTransform(offsetX, offsetY));
                }

                drawingContext.PushTransform(transform);
                DrawGlyph(
                    drawingContext,
                    glyph,
                    style,
                    absoluteBounds,
                    canvasScale * Math.Max(0.05, Scale),
                    globalOpacity * opacity,
                    highlight,
                    fill);
                drawingContext.Pop();
            }
        }

        private void DrawGlyph(
            DrawingContext drawingContext,
            GlyphBox glyph,
            StyleProfile style,
            Rect absoluteBounds,
            double scaleFactor,
            double opacity,
            Brush? highlightBrush,
            Brush? fillBrush)
        {
            Geometry geometry = glyph.Geometry;

            // Layer order: shadows first (font group, then composition group), then outlines,
            // then the fill on top so the glyph edges always stay crisp.
            DrawShadows(drawingContext, geometry, style, absoluteBounds, scaleFactor, opacity, EffectTarget.Font);
            DrawShadows(drawingContext, geometry, style, absoluteBounds, scaleFactor, opacity, EffectTarget.Composition);
            DrawOutlines(drawingContext, geometry, style, absoluteBounds, scaleFactor, opacity, EffectTarget.Font);
            DrawOutlines(drawingContext, geometry, style, absoluteBounds, scaleFactor, opacity, EffectTarget.Composition);

            bool highlighted = style.HighlightSpokenWord && glyph.WordIndex == activeWordIndex;
            Brush? brush = highlighted && highlightBrush != null ? highlightBrush : fillBrush;
            if (brush != null)
            {
                drawingContext.DrawGeometry(brush, null, geometry);
            }
        }

        private static void DrawShadows(
            DrawingContext drawingContext,
            Geometry geometry,
            StyleProfile style,
            Rect absoluteBounds,
            double scaleFactor,
            double opacity,
            string target)
        {
            List<ShadowLayer> shadows = style.Shadows;
            for (int index = 0; index < shadows.Count; index++)
            {
                ShadowLayer shadow = shadows[index];
                if (!shadow.Enabled || !MatchesTarget(shadow.Target, target))
                {
                    continue;
                }

                double thickness = Math.Max(0.0, shadow.Thickness * scaleFactor);
                double offsetX = shadow.OffsetX * scaleFactor;
                double offsetY = shadow.OffsetY * scaleFactor;
                double layerOpacity = Math.Max(0.0, Math.Min(1.0, shadow.Opacity)) * opacity;

                drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));

                if (thickness > 0.6)
                {
                    Brush? soft = StyleBrushes.CreateBrush(shadow.Color, absoluteBounds, layerOpacity * 0.45);
                    if (soft != null)
                    {
                        Pen pen = new Pen(soft, thickness * 2.0);
                        pen.LineJoin = PenLineJoin.Round;
                        pen.StartLineCap = PenLineCap.Round;
                        pen.EndLineCap = PenLineCap.Round;
                        pen.Freeze();
                        drawingContext.DrawGeometry(null, pen, geometry);
                    }
                }

                Brush? core = StyleBrushes.CreateBrush(shadow.Color, absoluteBounds, layerOpacity);
                if (core != null)
                {
                    drawingContext.DrawGeometry(core, null, geometry);
                }

                drawingContext.Pop();
            }
        }

        private static void DrawOutlines(
            DrawingContext drawingContext,
            Geometry geometry,
            StyleProfile style,
            Rect absoluteBounds,
            double scaleFactor,
            double opacity,
            string target)
        {
            List<OutlineLayer> outlines = style.Outlines;
            for (int index = 0; index < outlines.Count; index++)
            {
                OutlineLayer outline = outlines[index];
                if (!outline.Enabled || !MatchesTarget(outline.Target, target))
                {
                    continue;
                }

                double width = Math.Max(0.0, outline.Thickness * scaleFactor) * 2.0;
                if (width <= 0.2)
                {
                    continue;
                }

                Brush? brush = StyleBrushes.CreateBrush(outline.Color, absoluteBounds, opacity);
                if (brush == null)
                {
                    continue;
                }

                Pen pen = new Pen(brush, width);
                pen.LineJoin = PenLineJoin.Round;
                pen.StartLineCap = PenLineCap.Round;
                pen.EndLineCap = PenLineCap.Round;
                pen.Freeze();

                double offsetX = outline.OffsetX * scaleFactor;
                double offsetY = outline.OffsetY * scaleFactor;
                bool shifted = Math.Abs(offsetX) > 0.001 || Math.Abs(offsetY) > 0.001;

                if (shifted)
                {
                    drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
                }

                drawingContext.DrawGeometry(null, pen, geometry);

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
            List<WordTiming> visible = SubtitleTextPlan.WindowWords(words, activeWordIndex, style.WordsPerDisplay);
            string display = SubtitleTextPlan.JoinWords(visible);

            PopInProfile? popIn = PopIn;
            bool perCharacterEnter = popIn != null && popIn.PerCharacter
                && !string.Equals(popIn.Kind, PopInKind.None, StringComparison.OrdinalIgnoreCase);
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
                perCharacterEnter ? "1" : "0",
                "|",
                characterDelay.ToString("0.###", CultureInfo.InvariantCulture),
                "|",
                CueStartSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "|",
                activeWordIndex.ToString(CultureInfo.InvariantCulture),
                "|",
                style.WordsPerDisplay.ToString(CultureInfo.InvariantCulture));

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

            double cursorX = 0.0;
            double lineHeight = fontSize * 1.35;
            int globalIndex = 0;
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
                            box.Index = globalIndex;
                            double appearBase = style.WordsPerDisplay > 0
                                ? word.StartSeconds
                                : CueStartSeconds;
                            box.AppearSeconds = perCharacterEnter
                                ? appearBase + (wordPosition * characterDelay)
                                : appearBase;
                            glyphs.Add(box);
                            union = union.IsEmpty ? bounds : Rect.Union(union, bounds);
                        }
                    }

                    if (!char.IsWhiteSpace(symbol[0]))
                    {
                        wordPosition++;
                    }

                    cursorX += advance;
                    globalIndex++;
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
                    cursorX += space.WidthIncludingTrailingWhitespace;
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
