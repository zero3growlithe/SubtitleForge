using System;
using System.Collections.Generic;
using System.Text;
using SubtitleForge.Models;

namespace SubtitleForge.Services
{
    public class CueSplitOptions
    {
        public int MaximumCharactersPerCue { get; set; } = 48;

        public double MaximumCueDurationSeconds { get; set; } = 7.0;

        public double MinimumCueDurationSeconds { get; set; } = 0.6;

        public double MinimumGapSeconds { get; set; } = 0.04;
    }

    /// <summary>
    /// Turns raw Whisper segments into readable cues: wraps long text, splits long
    /// segments proportionally, removes overlaps and renumbers everything.
    /// </summary>
    public static class CuePostProcessor
    {
        /// <summary>The shortest edit a start or end drag may leave behind.</summary>
        private static readonly TimeSpan MinimumCueDuration = TimeSpan.FromMilliseconds(10.0);

        public static List<SubtitleCue> Normalize(IEnumerable<SubtitleCue> source, CueSplitOptions options)
        {
            List<SubtitleCue> sorted = new List<SubtitleCue>();
            foreach (SubtitleCue cue in source)
            {
                if (string.IsNullOrWhiteSpace(cue.Text))
                {
                    continue;
                }

                sorted.Add(cue);
            }

            sorted.Sort(delegate (SubtitleCue left, SubtitleCue right)
            {
                return left.Start.CompareTo(right.Start);
            });

            List<SubtitleCue> result = new List<SubtitleCue>();
            foreach (SubtitleCue cue in sorted)
            {
                string text = cue.Text.Trim();
                double duration = cue.Duration.TotalSeconds;
                List<string> chunks = WrapIntoChunks(text, options.MaximumCharactersPerCue);

                bool needsSplit = chunks.Count > 1
                    && (text.Length > options.MaximumCharactersPerCue || duration > options.MaximumCueDurationSeconds);

                if (!needsSplit)
                {
                    result.Add(new SubtitleCue(cue.Start, cue.End, text, cue.Confidence));
                    continue;
                }

                int totalCharacters = 0;
                foreach (string chunk in chunks)
                {
                    totalCharacters += Math.Max(1, chunk.Length);
                }

                double cursor = cue.Start.TotalSeconds;
                foreach (string chunk in chunks)
                {
                    double share = duration * (Math.Max(1, chunk.Length) / (double)totalCharacters);
                    double chunkDuration = share;
                    if (chunkDuration < options.MinimumCueDurationSeconds)
                    {
                        chunkDuration = options.MinimumCueDurationSeconds;
                    }

                    if (chunkDuration > options.MaximumCueDurationSeconds)
                    {
                        chunkDuration = options.MaximumCueDurationSeconds;
                    }

                    TimeSpan chunkStart = TimeSpan.FromSeconds(cursor);
                    TimeSpan chunkEnd = TimeSpan.FromSeconds(cursor + chunkDuration);
                    result.Add(new SubtitleCue(chunkStart, chunkEnd, chunk, cue.Confidence));
                    cursor += chunkDuration;
                }
            }

            RemoveOverlaps(result, options.MinimumGapSeconds);
            Renumber(result);
            return result;
        }

        public static void Renumber(IList<SubtitleCue> cues)
        {
            for (int index = 0; index < cues.Count; index++)
            {
                cues[index].Number = index + 1;
            }
        }

        /// <summary>
        /// Splits one cue into two at the midpoint of its words, sharing the time range.
        /// </summary>
        public static List<SubtitleCue> SplitCue(SubtitleCue cue)
        {
            List<SubtitleCue> parts = new List<SubtitleCue>();
            string[] words = cue.Text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2)
            {
                parts.Add(cue.Clone());
                return parts;
            }

            int middle = words.Length / 2;
            StringBuilder first = new StringBuilder();
            StringBuilder second = new StringBuilder();

            for (int index = 0; index < words.Length; index++)
            {
                if (index < middle)
                {
                    if (first.Length > 0)
                    {
                        first.Append(' ');
                    }

                    first.Append(words[index]);
                }
                else
                {
                    if (second.Length > 0)
                    {
                        second.Append(' ');
                    }

                    second.Append(words[index]);
                }
            }

            TimeSpan splitPoint = cue.Start + TimeSpan.FromMilliseconds(cue.Duration.TotalMilliseconds * 0.5);
            if (splitPoint <= cue.Start)
            {
                splitPoint = cue.Start + TimeSpan.FromMilliseconds(200);
            }

            if (splitPoint >= cue.End)
            {
                splitPoint = cue.End - TimeSpan.FromMilliseconds(1);
            }

            parts.Add(new SubtitleCue(cue.Start, splitPoint, first.ToString(), cue.Confidence));
            parts.Add(new SubtitleCue(splitPoint, cue.End, second.ToString(), cue.Confidence));
            return parts;
        }

        /// <summary>
        /// Moves the start of a cue and leaves the end alone, keeping a tiny minimum duration so the
        /// cue cannot turn inside out.
        /// </summary>
        public static void ShiftCueStart(SubtitleCue cue, TimeSpan delta)
        {
            TimeSpan start = cue.Start + delta;
            if (start < TimeSpan.Zero)
            {
                start = TimeSpan.Zero;
            }

            TimeSpan latestStart = cue.End - MinimumCueDuration;
            if (start > latestStart)
            {
                start = latestStart;
            }

            if (start < TimeSpan.Zero)
            {
                start = TimeSpan.Zero;
            }

            cue.Start = start;
        }

        /// <summary>Moves the end of a cue and leaves the start alone.</summary>
        public static void ShiftCueEnd(SubtitleCue cue, TimeSpan delta)
        {
            TimeSpan end = cue.End + delta;
            TimeSpan earliestEnd = cue.Start + MinimumCueDuration;
            if (end < earliestEnd)
            {
                end = earliestEnd;
            }

            cue.End = end;
        }

        public static void ShiftCue(SubtitleCue cue, TimeSpan delta)
        {
            TimeSpan start = cue.Start + delta;
            TimeSpan end = cue.End + delta;
            if (start < TimeSpan.Zero)
            {
                start = TimeSpan.Zero;
            }

            if (end <= start)
            {
                end = start + TimeSpan.FromMilliseconds(100);
            }

            cue.Start = start;
            cue.End = end;
        }

        public static SubtitleCue Merge(SubtitleCue first, SubtitleCue second)
        {
            TimeSpan start = first.Start < second.Start ? first.Start : second.Start;
            TimeSpan end = first.End > second.End ? first.End : second.End;

            string text;
            if (string.Equals(first.Text.Trim(), second.Text.Trim(), StringComparison.Ordinal))
            {
                text = first.Text.Trim();
            }
            else
            {
                text = (first.Text.Trim() + " " + second.Text.Trim()).Trim();
            }

            double confidence = (first.Confidence + second.Confidence) / 2.0;
            return new SubtitleCue(start, end, text, confidence);
        }

        private static void RemoveOverlaps(List<SubtitleCue> cues, double minimumGapSeconds)
        {
            for (int index = 0; index < cues.Count - 1; index++)
            {
                SubtitleCue current = cues[index];
                SubtitleCue next = cues[index + 1];
                double limit = next.Start.TotalSeconds - minimumGapSeconds;

                if (current.End.TotalSeconds <= limit)
                {
                    continue;
                }

                double proposedEnd = Math.Max(current.Start.TotalSeconds + 0.2, limit);
                if (proposedEnd > current.Start.TotalSeconds)
                {
                    current.End = TimeSpan.FromSeconds(proposedEnd);
                }
                else
                {
                    double newStart = current.End.TotalSeconds + minimumGapSeconds;
                    if (newStart > next.Start.TotalSeconds || current.End > next.Start)
                    {
                        next.Start = TimeSpan.FromSeconds(newStart);
                    }
                }
            }

            foreach (SubtitleCue cue in cues)
            {
                if (cue.End <= cue.Start)
                {
                    cue.End = cue.Start + TimeSpan.FromMilliseconds(100);
                }
            }
        }

        private static List<string> WrapIntoChunks(string text, int maximumCharacters)
        {
            List<string> chunks = new List<string>();
            string[] paragraphs = text.Replace("\r", string.Empty).Split('\n');

            foreach (string paragraph in paragraphs)
            {
                string trimmed = paragraph.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (trimmed.Length <= maximumCharacters)
                {
                    chunks.Add(trimmed);
                    continue;
                }

                string[] words = trimmed.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                StringBuilder current = new StringBuilder();

                foreach (string word in words)
                {
                    if (word.Length > maximumCharacters)
                    {
                        if (current.Length > 0)
                        {
                            chunks.Add(current.ToString());
                            current.Clear();
                        }

                        int offset = 0;
                        while (offset < word.Length)
                        {
                            int take = Math.Min(maximumCharacters, word.Length - offset);
                            chunks.Add(word.Substring(offset, take));
                            offset += take;
                        }

                        continue;
                    }

                    int projected = current.Length == 0 ? word.Length : current.Length + 1 + word.Length;
                    if (projected > maximumCharacters)
                    {
                        chunks.Add(current.ToString());
                        current.Clear();
                    }

                    if (current.Length > 0)
                    {
                        current.Append(' ');
                    }

                    current.Append(word);
                }

                if (current.Length > 0)
                {
                    chunks.Add(current.ToString());
                }
            }

            if (chunks.Count == 0)
            {
                chunks.Add(text.Trim());
            }

            return chunks;
        }
    }
}
