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
    /// Turns what Whisper produced into readable cues. Whisper hands back either one segment per
    /// phrase or one cue per word (when it gave token timestamps), and this class is what makes
    /// both look the same: anything longer than the character or second budget is cut into pieces
    /// that stay inside its own time range, neighbouring pieces are glued back together while they
    /// still fit, a very short cue is held a little longer into the silence that follows it, and
    /// overlaps are trimmed at the end.
    /// </summary>
    public static class CuePostProcessor
    {
        /// <summary>The shortest edit a start or end drag may leave behind.</summary>
        private static readonly TimeSpan MinimumCueDuration = TimeSpan.FromMilliseconds(10.0);

        /// <summary>
        /// Silence longer than this starts a new cue instead of extending the one on screen, so a
        /// two-word cue never reaches across a pause.
        /// </summary>
        private const double WordGroupingGapSeconds = 0.35;

        private static readonly char[] SentenceEnds = new char[] { '.', '!', '?', '…', ':', ';' };

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

            // Cut first, glue second. A word-level cue is already as short as it gets and passes
            // through untouched; a whole phrase Whisper returned without word timings is cut into
            // pieces that fit, and the glue pass cannot put them back together because their joined
            // length is over the character budget by definition.
            List<SubtitleCue> pieces = new List<SubtitleCue>();
            foreach (SubtitleCue cue in sorted)
            {
                SplitIntoPieces(cue, options, pieces);
            }

            List<SubtitleCue> result = GroupPieces(pieces, options);
            HoldShortCues(result, options);
            RemoveOverlaps(result, options.MinimumGapSeconds);
            Renumber(result);
            return result;
        }

        /// <summary>
        /// Appends one cue, or the pieces it has to be cut into, to <paramref name="destination"/>.
        /// The pieces share the cue's time range by character count and never leave it: stretching
        /// each piece to the minimum duration is what used to push a run of short cues past the end
        /// of the phrase they came from, so everything after it started late and the subtitles fell
        /// further behind the speech the more pieces there were.
        /// </summary>
        private static void SplitIntoPieces(SubtitleCue cue, CueSplitOptions options, List<SubtitleCue> destination)
        {
            string text = cue.Text.Trim();
            double duration = cue.Duration.TotalSeconds;
            List<string> chunks = WrapIntoChunks(text, options.MaximumCharactersPerCue);

            bool tooMuchText = text.Length > options.MaximumCharactersPerCue || chunks.Count > 1;
            bool onScreenTooLong = duration > options.MaximumCueDurationSeconds;

            // Text that already fits the character budget can still be on screen for too long: a
            // short line over seven seconds is one line nobody needs. It is then cut by words so
            // "Max sec" means what it says instead of only applying to text that wraps anyway.
            if (!tooMuchText && onScreenTooLong)
            {
                List<string> byWords = SplitByWords(text, duration, options.MaximumCueDurationSeconds);
                if (byWords.Count > 1)
                {
                    chunks = byWords;
                }
            }

            bool needsSplit = chunks.Count > 1 && (tooMuchText || onScreenTooLong);

            if (!needsSplit || duration <= 0.0)
            {
                destination.Add(new SubtitleCue(cue.Start, cue.End, text, cue.Confidence));
                return;
            }

            int totalCharacters = 0;
            foreach (string chunk in chunks)
            {
                totalCharacters += Math.Max(1, chunk.Length);
            }

            double perChunkBudget = duration / chunks.Count;
            double minimum = Math.Min(options.MinimumCueDurationSeconds, perChunkBudget);

            double[] shares = new double[chunks.Count];
            double sum = 0.0;
            for (int index = 0; index < chunks.Count; index++)
            {
                double share = duration * (Math.Max(1, chunks[index].Length) / (double)totalCharacters);
                if (share < minimum)
                {
                    share = minimum;
                }

                shares[index] = share;
                sum += share;
            }

            // Renormalised so the last piece lands exactly on the end of the cue it came from.
            double scale = sum > 0.0001 ? duration / sum : 1.0;
            double cursor = cue.Start.TotalSeconds;
            double end = cue.End.TotalSeconds;

            for (int index = 0; index < chunks.Count; index++)
            {
                double remaining = end - cursor;
                if (remaining <= 0.0005)
                {
                    break;
                }

                double chunkDuration = Math.Min(shares[index] * scale, options.MaximumCueDurationSeconds);
                if (chunkDuration > remaining)
                {
                    chunkDuration = remaining;
                }

                if (chunkDuration < 0.05)
                {
                    chunkDuration = Math.Min(0.05, remaining);
                }

                destination.Add(new SubtitleCue(
                    TimeSpan.FromSeconds(cursor),
                    TimeSpan.FromSeconds(cursor + chunkDuration),
                    chunks[index],
                    cue.Confidence));
                cursor += chunkDuration;
            }
        }

        /// <summary>
        /// Cuts a line that has little text but sits on screen far too long into word groups, as
        /// many as the duration needs to stay under the maximum. Returns an empty list when the line
        /// has a single word and there is nothing to cut.
        /// </summary>
        private static List<string> SplitByWords(string text, double duration, double maximumSeconds)
        {
            List<string> groups = new List<string>();
            string[] words = text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || maximumSeconds <= 0.0)
            {
                return groups;
            }

            int wanted = (int)Math.Ceiling(duration / maximumSeconds);
            if (wanted < 2)
            {
                wanted = 2;
            }

            if (wanted > words.Length)
            {
                wanted = words.Length;
            }

            int perGroup = Math.Max(1, (int)Math.Ceiling(words.Length / (double)wanted));
            StringBuilder current = new StringBuilder();
            int wordIndex = 0;
            for (int group = 0; group < wanted; group++)
            {
                current.Clear();
                for (int taken = 0; taken < perGroup && wordIndex < words.Length; taken++)
                {
                    if (current.Length > 0)
                    {
                        current.Append(' ');
                    }

                    current.Append(words[wordIndex]);
                    wordIndex++;
                }

                if (current.Length > 0)
                {
                    groups.Add(current.ToString());
                }
            }

            if (wordIndex < words.Length)
            {
                // Anything left over joins the last group rather than being dropped.
                if (groups.Count == 0)
                {
                    groups.Add(text);
                }
                else
                {
                    StringBuilder rest = new StringBuilder(groups[groups.Count - 1]);
                    while (wordIndex < words.Length)
                    {
                        rest.Append(' ');
                        rest.Append(words[wordIndex]);
                        wordIndex++;
                    }

                    groups[groups.Count - 1] = rest.ToString();
                }
            }

            return groups;
        }

        /// <summary>
        /// Glues neighbouring pieces into one cue while the result still fits the character and
        /// second budgets and no pause or full stop comes between them. This is what turns one cue
        /// per word from Whisper into two to four words on screen, timed by the words themselves.
        /// </summary>
        private static List<SubtitleCue> GroupPieces(List<SubtitleCue> pieces, CueSplitOptions options)
        {
            List<SubtitleCue> grouped = new List<SubtitleCue>();
            SubtitleCue? current = null;

            foreach (SubtitleCue piece in pieces)
            {
                string text = piece.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                if (current == null)
                {
                    current = new SubtitleCue(piece.Start, piece.End, text, piece.Confidence);
                    continue;
                }

                string joined = current.Text + " " + text;
                double mergedSeconds = (piece.End - current.Start).TotalSeconds;
                double gapSeconds = (piece.Start - current.End).TotalSeconds;

                bool joinable = joined.Length <= options.MaximumCharactersPerCue
                    && mergedSeconds <= options.MaximumCueDurationSeconds
                    && gapSeconds <= WordGroupingGapSeconds
                    && !EndsSentence(current.Text);

                if (joinable)
                {
                    current.Text = joined;
                    current.End = piece.End;
                    continue;
                }

                grouped.Add(current);
                current = new SubtitleCue(piece.Start, piece.End, text, piece.Confidence);
            }

            if (current != null)
            {
                grouped.Add(current);
            }

            return grouped;
        }

        private static bool EndsSentence(string text)
        {
            for (int index = text.Length - 1; index >= 0; index--)
            {
                char character = text[index];
                if (char.IsWhiteSpace(character))
                {
                    continue;
                }

                for (int ends = 0; ends < SentenceEnds.Length; ends++)
                {
                    if (SentenceEnds[ends] == character)
                    {
                        return true;
                    }
                }

                return false;
            }

            return false;
        }

        /// <summary>
        /// Holds a cue that is shorter than the minimum on screen a little longer, but only into the
        /// silence that follows it: it never pushes the next cue's start.
        /// </summary>
        private static void HoldShortCues(List<SubtitleCue> cues, CueSplitOptions options)
        {
            for (int index = 0; index < cues.Count; index++)
            {
                SubtitleCue cue = cues[index];
                if (cue.Duration.TotalSeconds >= options.MinimumCueDurationSeconds)
                {
                    continue;
                }

                double limit = cue.Start.TotalSeconds + options.MinimumCueDurationSeconds;
                if (index + 1 < cues.Count)
                {
                    double nextStart = cues[index + 1].Start.TotalSeconds - options.MinimumGapSeconds;
                    if (limit > nextStart)
                    {
                        limit = nextStart;
                    }
                }

                if (limit > cue.End.TotalSeconds)
                {
                    cue.End = TimeSpan.FromSeconds(limit);
                }
            }
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
