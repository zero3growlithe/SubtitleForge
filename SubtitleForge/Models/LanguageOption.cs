using System;

namespace SubtitleForge.Models
{
    /// <summary>
    /// A Whisper language code plus the label shown in the language combo box.
    /// </summary>
    public class LanguageOption
    {
        public LanguageOption(string code, string display)
        {
            Code = code;
            Display = display;
        }

        public string Code { get; }

        public string Display { get; }

        public override string ToString()
        {
            return Display;
        }
    }
}
