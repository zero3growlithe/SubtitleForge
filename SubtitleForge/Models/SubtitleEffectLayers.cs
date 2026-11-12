using System.Collections.Generic;

namespace SubtitleForge.Models
{
    /// <summary>Where an outline or shadow is applied.</summary>
    public static class EffectTarget
    {
        /// <summary>Applied to the glyphs themselves, underneath everything else.</summary>
        public const string Font = "Font";

        /// <summary>Applied around the finished font + outlines + shadows composition.</summary>
        public const string Composition = "Composition";
    }

    /// <summary>One outline ring. Several of them stack in list order.</summary>
    public sealed class OutlineLayer
    {
        public string Name { get; set; } = "Outline";

        public bool Enabled { get; set; } = true;

        public ColorSpec Color { get; set; } = ColorSpec.Solid("#FF000000");

        /// <summary>Stroke width in design pixels (1080-tall canvas).</summary>
        public double Thickness { get; set; } = 3.0;

        public double OffsetX { get; set; }

        public double OffsetY { get; set; }

        public string Target { get; set; } = EffectTarget.Font;

        public OutlineLayer Clone()
        {
            OutlineLayer copy = new OutlineLayer();
            copy.Name = Name;
            copy.Enabled = Enabled;
            copy.Color = Color.Clone();
            copy.Thickness = Thickness;
            copy.OffsetX = OffsetX;
            copy.OffsetY = OffsetY;
            copy.Target = Target;
            return copy;
        }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Name) ? "Outline" : Name;
        }

        public static List<OutlineLayer> CreateDefaults()
        {
            List<OutlineLayer> layers = new List<OutlineLayer>();
            OutlineLayer outline = new OutlineLayer();
            outline.Name = "Outline";
            outline.Color = ColorSpec.Solid("#FF000000");
            outline.Thickness = 3.0;
            outline.Target = EffectTarget.Font;
            layers.Add(outline);
            return layers;
        }
    }

    /// <summary>One soft shadow. Several of them stack in list order.</summary>
    public sealed class ShadowLayer
    {
        public string Name { get; set; } = "Shadow";

        public bool Enabled { get; set; } = true;

        public ColorSpec Color { get; set; } = new ColorSpec("#CC000000", "#00000000", ColorSpec.DirectionTopToBottom);

        /// <summary>How far the shadow is spread/softened, in design pixels.</summary>
        public double Thickness { get; set; } = 4.0;

        public double OffsetX { get; set; } = 3.0;

        public double OffsetY { get; set; } = 4.0;

        public double Opacity { get; set; } = 0.85;

        public string Target { get; set; } = EffectTarget.Font;

        public ShadowLayer Clone()
        {
            ShadowLayer copy = new ShadowLayer();
            copy.Name = Name;
            copy.Enabled = Enabled;
            copy.Color = Color.Clone();
            copy.Thickness = Thickness;
            copy.OffsetX = OffsetX;
            copy.OffsetY = OffsetY;
            copy.Opacity = Opacity;
            copy.Target = Target;
            return copy;
        }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Name) ? "Shadow" : Name;
        }

        public static List<ShadowLayer> CreateDefaults()
        {
            List<ShadowLayer> layers = new List<ShadowLayer>();
            ShadowLayer shadow = new ShadowLayer();
            shadow.Name = "Shadow";
            shadow.Color = new ColorSpec("#CC000000", "#00000000", ColorSpec.DirectionTopToBottom);
            shadow.Thickness = 4.0;
            shadow.OffsetX = 3.0;
            shadow.OffsetY = 4.0;
            shadow.Opacity = 0.85;
            shadow.Target = EffectTarget.Font;
            layers.Add(shadow);
            return layers;
        }
    }
}
