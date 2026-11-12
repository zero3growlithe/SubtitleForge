using System;
using System.Collections.Generic;

namespace SubtitleForge.Models
{
    /// <summary>How the subtitle text itself looks: font, fill, outlines and shadows.</summary>
    public sealed class StyleProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "Style";

        public string FontFamily { get; set; } = "Segoe UI";

        public bool Bold { get; set; } = true;

        public bool Italic { get; set; }

        /// <summary>Font size in pixels on a 1080-tall canvas; the overlay scales it.</summary>
        public double FontSize { get; set; } = 64.0;

        /// <summary>Extra gap after every glyph, in design pixels.</summary>
        public double CharacterSpacing { get; set; }

        /// <summary>Extra gap between words, in design pixels, on top of the character spacing.</summary>
        public double WordSpacing { get; set; }

        public ColorSpec Fill { get; set; } = ColorSpec.Solid("#FFFFFFFF");

        /// <summary>Outlines and shadows, painted in list order (first entry sits closest to the fill).</summary>
        public List<EffectLayer> Effects { get; set; } = EffectLayer.CreateDefaults();

        /// <summary>Legacy outline stack, only read from profile files written before the merge.</summary>
        public List<OutlineLayer>? Outlines { get; set; }

        /// <summary>Legacy shadow stack, only read from profile files written before the merge.</summary>
        public List<ShadowLayer>? Shadows { get; set; }

        /// <summary>0 shows the whole cue at once; N shows N words at a time.</summary>
        public int WordsPerDisplay { get; set; }

        public bool HighlightSpokenWord { get; set; }

        public ColorSpec HighlightFill { get; set; } = ColorSpec.Solid("#FFFFD54A");

        /// <summary>Opacity of the text block, 0-1.</summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>
        /// Folds a legacy two-stack profile into the single effect list and makes sure the list is
        /// never null. Call after deserialising and after cloning a profile from an older file.
        /// </summary>
        public void Normalize()
        {
            if (Outlines != null || Shadows != null)
            {
                List<EffectLayer> converted = new List<EffectLayer>();

                // Shadows used to be painted before outlines, so keeping that order preserves the
                // look of an existing profile.
                if (Shadows != null)
                {
                    foreach (ShadowLayer shadow in Shadows)
                    {
                        converted.Add(EffectLayer.FromLegacyShadow(shadow));
                    }
                }

                if (Outlines != null)
                {
                    foreach (OutlineLayer outline in Outlines)
                    {
                        converted.Add(EffectLayer.FromLegacyOutline(outline));
                    }
                }

                Effects = converted;
                Outlines = null;
                Shadows = null;
            }

            if (Effects == null)
            {
                Effects = EffectLayer.CreateDefaults();
            }
        }

        public StyleProfile Clone()
        {
            StyleProfile copy = new StyleProfile();
            copy.Id = Id;
            copy.Name = Name;
            copy.FontFamily = FontFamily;
            copy.Bold = Bold;
            copy.Italic = Italic;
            copy.FontSize = FontSize;
            copy.CharacterSpacing = CharacterSpacing;
            copy.WordSpacing = WordSpacing;
            copy.Fill = Fill.Clone();
            copy.Effects = new List<EffectLayer>();
            foreach (EffectLayer effect in Effects)
            {
                copy.Effects.Add(effect.Clone());
            }

            copy.WordsPerDisplay = WordsPerDisplay;
            copy.HighlightSpokenWord = HighlightSpokenWord;
            copy.HighlightFill = HighlightFill.Clone();
            copy.Opacity = Opacity;
            return copy;
        }
    }
}
