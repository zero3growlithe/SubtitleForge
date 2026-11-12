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

        public ColorSpec Fill { get; set; } = ColorSpec.Solid("#FFFFFFFF");

        public List<OutlineLayer> Outlines { get; set; } = OutlineLayer.CreateDefaults();

        public List<ShadowLayer> Shadows { get; set; } = ShadowLayer.CreateDefaults();

        /// <summary>0 shows the whole cue at once; N shows N words at a time.</summary>
        public int WordsPerDisplay { get; set; }

        public bool HighlightSpokenWord { get; set; }

        public ColorSpec HighlightFill { get; set; } = ColorSpec.Solid("#FFFFD54A");

        /// <summary>Opacity of the text block, 0-1.</summary>
        public double Opacity { get; set; } = 1.0;

        public StyleProfile Clone()
        {
            StyleProfile copy = new StyleProfile();
            copy.Id = Id;
            copy.Name = Name;
            copy.FontFamily = FontFamily;
            copy.Bold = Bold;
            copy.Italic = Italic;
            copy.FontSize = FontSize;
            copy.Fill = Fill.Clone();
            copy.Outlines = new List<OutlineLayer>();
            foreach (OutlineLayer outline in Outlines)
            {
                copy.Outlines.Add(outline.Clone());
            }

            copy.Shadows = new List<ShadowLayer>();
            foreach (ShadowLayer shadow in Shadows)
            {
                copy.Shadows.Add(shadow.Clone());
            }

            copy.WordsPerDisplay = WordsPerDisplay;
            copy.HighlightSpokenWord = HighlightSpokenWord;
            copy.HighlightFill = HighlightFill.Clone();
            copy.Opacity = Opacity;
            return copy;
        }
    }
}
