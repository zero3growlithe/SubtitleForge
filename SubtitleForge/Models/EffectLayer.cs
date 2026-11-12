using System;
using System.Collections.Generic;

namespace SubtitleForge.Models
{
    /// <summary>The two kinds of layer that can sit under the text fill.</summary>
    public static class EffectKinds
    {
        public const string Outline = "Outline";

        public const string Shadow = "Shadow";
    }

    /// <summary>
    /// One entry of the style's effect stack. Outlines and shadows share a single list because they
    /// are painted in one pass, in list order — the only difference between them is which fields
    /// apply. A single list also means the order on screen is the order in the editor.
    /// </summary>
    public sealed class EffectLayer
    {
        public string Kind { get; set; } = EffectKinds.Outline;

        public string Name { get; set; } = "Outline";

        public bool Enabled { get; set; } = true;

        /// <summary><see cref="EffectTarget.Font"/> or <see cref="EffectTarget.Composition"/>.</summary>
        public string Target { get; set; } = EffectTarget.Font;

        public ColorSpec Color { get; set; } = ColorSpec.Solid("#FF000000");

        /// <summary>
        /// Outline: stroke width in design pixels (1080-tall canvas). A negative value draws the
        /// outline on the inside of the glyph. Shadow: how far the shadow is spread/softened.
        /// </summary>
        public double Thickness { get; set; } = 3.0;

        public double OffsetX { get; set; }

        public double OffsetY { get; set; }

        /// <summary>Shadows only: opacity of the layer, 0-1.</summary>
        public double Opacity { get; set; } = 0.85;

        /// <summary>Outlines only: draw the ring inside the glyph edge instead of around it.</summary>
        public bool Inside { get; set; }

        /// <summary>Outlines only: also fill the glyph with the outline colour as a base layer.</summary>
        public bool Filled { get; set; }

        public bool IsShadow
        {
            get { return string.Equals(Kind, EffectKinds.Shadow, StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsOutline
        {
            get { return !IsShadow; }
        }

        public EffectLayer Clone()
        {
            EffectLayer copy = new EffectLayer();
            copy.Kind = Kind;
            copy.Name = Name;
            copy.Enabled = Enabled;
            copy.Target = Target;
            copy.Color = Color.Clone();
            copy.Thickness = Thickness;
            copy.OffsetX = OffsetX;
            copy.OffsetY = OffsetY;
            copy.Opacity = Opacity;
            copy.Inside = Inside;
            copy.Filled = Filled;
            return copy;
        }

        public override string ToString()
        {
            string kind = IsShadow ? "Shadow" : "Outline";
            return string.IsNullOrWhiteSpace(Name) ? kind : Name;
        }

        public static EffectLayer CreateOutline()
        {
            EffectLayer effect = new EffectLayer();
            effect.Kind = EffectKinds.Outline;
            effect.Name = "Outline";
            effect.Target = EffectTarget.Font;
            effect.Color = ColorSpec.Solid("#FF000000");
            effect.Thickness = 3.0;
            effect.Opacity = 1.0;
            return effect;
        }

        public static EffectLayer CreateShadow()
        {
            EffectLayer effect = new EffectLayer();
            effect.Kind = EffectKinds.Shadow;
            effect.Name = "Shadow";
            effect.Target = EffectTarget.Font;
            effect.Color = new ColorSpec("#CC000000", "#00000000", ColorSpec.DirectionTopToBottom);
            effect.Thickness = 4.0;
            effect.OffsetX = 3.0;
            effect.OffsetY = 4.0;
            effect.Opacity = 0.85;
            return effect;
        }

        /// <summary>The stack a brand new profile starts with: one shadow behind one outline.</summary>
        public static List<EffectLayer> CreateDefaults()
        {
            List<EffectLayer> effects = new List<EffectLayer>();
            effects.Add(CreateShadow());
            effects.Add(CreateOutline());
            return effects;
        }

        /// <summary>Converts a profile written before the stacks were merged into one list.</summary>
        public static EffectLayer FromLegacyOutline(OutlineLayer outline)
        {
            EffectLayer effect = CreateOutline();
            effect.Name = outline.Name;
            effect.Enabled = outline.Enabled;
            effect.Target = outline.Target;
            effect.Color = outline.Color.Clone();
            effect.Thickness = outline.Thickness;
            effect.OffsetX = outline.OffsetX;
            effect.OffsetY = outline.OffsetY;
            effect.Inside = outline.Inside;
            effect.Filled = outline.Filled;
            effect.Opacity = 1.0;
            return effect;
        }

        public static EffectLayer FromLegacyShadow(ShadowLayer shadow)
        {
            EffectLayer effect = CreateShadow();
            effect.Name = shadow.Name;
            effect.Enabled = shadow.Enabled;
            effect.Target = shadow.Target;
            effect.Color = shadow.Color.Clone();
            effect.Thickness = shadow.Thickness;
            effect.OffsetX = shadow.OffsetX;
            effect.OffsetY = shadow.OffsetY;
            effect.Opacity = shadow.Opacity;
            return effect;
        }
    }
}
