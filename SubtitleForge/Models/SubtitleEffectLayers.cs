using System.Collections.Generic;

namespace SubtitleForge.Models
{
    /// <summary>Where an outline or shadow is applied.</summary>
    public static class EffectTarget
    {
        /// <summary>Applied to each glyph separately, underneath the fill.</summary>
        public const string Font = "Font";

        /// <summary>
        /// Applied once around the whole rendered block: the glyphs are combined into one silhouette
        /// before the effect is drawn, so the ring wraps the line instead of every letter.
        /// </summary>
        public const string Composition = "Composition";
    }

    /// <summary>
    /// Legacy outline entry. A profile now holds a single <see cref="EffectLayer"/> stack; this type
    /// only exists so an older styles.json still loads, and it is converted on read.
    /// </summary>
    public sealed class OutlineLayer
    {
        public string Name { get; set; } = "Outline";

        public bool Enabled { get; set; } = true;

        public ColorSpec Color { get; set; } = ColorSpec.Solid("#FF000000");

        public double Thickness { get; set; } = 3.0;

        public double OffsetX { get; set; }

        public double OffsetY { get; set; }

        public bool Inside { get; set; }

        public bool Filled { get; set; }

        public string Target { get; set; } = EffectTarget.Font;
    }

    /// <summary>
    /// Legacy shadow entry. As above: read-only compatibility, converted into an
    /// <see cref="EffectLayer"/> by <see cref="StyleProfile.Normalize"/>.
    /// </summary>
    public sealed class ShadowLayer
    {
        public string Name { get; set; } = "Shadow";

        public bool Enabled { get; set; } = true;

        public ColorSpec Color { get; set; } = new ColorSpec("#CC000000", "#00000000", ColorSpec.DirectionTopToBottom);

        public double Thickness { get; set; } = 4.0;

        public double OffsetX { get; set; } = 3.0;

        public double OffsetY { get; set; } = 4.0;

        public double Opacity { get; set; } = 0.85;

        public string Target { get; set; } = EffectTarget.Font;
    }
}
