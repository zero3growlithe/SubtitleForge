using System;

namespace SubtitleForge.Models
{
    /// <summary>
    /// Where a pop-in or a pop-out plays relative to the cue's own time window.
    ///
    /// <para><b>Outside</b> (the default) puts the animation where there is no subtitle text yet or
    /// any more: an entrance finishes exactly on the cue's start, so the line is already fully
    /// popped in the moment it is due, and an exit begins on the cue's end, so the line starts
    /// leaving when its time is up. The animation therefore does not eat into the time the words are
    /// on screen.</para>
    ///
    /// <para><b>Inside</b> keeps the animation within the cue: the entrance begins on the cue's
    /// start and the exit ends on the cue's end, which is what a short cue compresses.</para>
    /// </summary>
    public static class PopTiming
    {
        /// <summary>The animation sits outside the cue: it finishes on the cue's edge.</summary>
        public const string OutsideCue = "OutsideCue";

        /// <summary>The animation sits inside the cue: it starts on the cue's edge.</summary>
        public const string InsideCue = "InsideCue";

        /// <summary>
        /// True unless the timing says otherwise, so a profile saved before this option existed keeps
        /// working and gets the behaviour the option was added for.
        /// </summary>
        public static bool IsOutside(string? timing)
        {
            return !string.Equals(timing, InsideCue, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsInside(string? timing)
        {
            return string.Equals(timing, InsideCue, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// How many groups a stagger walks over: one for a whole-cue stagger, one per word, or one per
        /// character. Shared by every caller that has to know how long an animation can run before it
        /// has laid any text out.
        /// </summary>
        public static int GroupCount(bool perCharacter, string? grouping, string? text)
        {
            if (!perCharacter
                || string.Equals(grouping, StaggerGroupings.Cue, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            string body = text ?? string.Empty;
            if (string.Equals(grouping, StaggerGroupings.Word, StringComparison.OrdinalIgnoreCase))
            {
                int words = 0;
                bool insideWord = false;
                for (int index = 0; index < body.Length; index++)
                {
                    if (char.IsWhiteSpace(body[index]))
                    {
                        insideWord = false;
                    }
                    else if (!insideWord)
                    {
                        insideWord = true;
                        words++;
                    }
                }

                return Math.Max(1, words);
            }

            return Math.Max(1, body.Length);
        }

        /// <summary>The group count a cue's text can possibly need, for bounding a scan of the cue list.</summary>
        public const int MaximumGroups = 400;
    }
}
