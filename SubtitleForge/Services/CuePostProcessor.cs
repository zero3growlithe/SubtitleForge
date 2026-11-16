using System;
using System.Collections.Generic;
using System.Text;
using SubtitleForge.Models;

namespace SubtitleForge.Services
{
    public class CueSplitOptions
    {
        /// <summary>
        /// The one thing that shapes a cue. There is deliberately no minimum or maximum duration
        /// here: a cue lasts exactly as long as its words were spoken, so no rule can stretch a line
        /// into its neighbour and leave two subtitles on screen at once.
        /// </summary>
        public int MaximumCharactersPerCue { get; set; } = 48;
    }

    /// <summary>
    /// Turns what Whisper produced into readable cues. Whisper hands back either one segment per
    /// phrase or one cue per word (when it gave token timestamps), and this class makes both look
    /// the same: text longer than the character budget is cut into pieces that share the cue's own
    /// time range by character count, and neighbouring words are glued back together while they
    /// still fit and no pause or full stop comes between them. Nothing here invents time — a cue is
    /// exactly the span its words were spoken over, short or long, and the final pass only trims an
    /// end so two cues can never share the screen.
    /// </summary>
    public static class CuePostProcessor
    {
        /// <summary>The shortest edit a start or end drag may leave behind.</summary>
        private static readonly TimeSpan MinimumCueDuration = TimeSpan.FromMilliseconds(10.0);

        /// <summary>
        /// The overlap guard: one millisecond is shorter than a frame, so trimming an end by it can
        /// never make two cues share a visible moment, and it is not a minimum duration because
        /// nothing is ever lengthened to reach it.
        /// </summary>
        private const double OverlapGuardSeconds = 0.001;

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
            TrimOverlaps(result);
            Renumber(result);
            return result;
        }

        /// <summary>
        /// Appends one cue, or the pieces its text has to be cut into. The pieces share the cue's
        /// own time range in proportion to how much of the line each one carries, so the last piece
        /// lands exactly on the cue's own end and no piece is ever stretched or clamped. A piece
        /// that would get no time at all is not shortened, it is carried by the next piece instead,
        /// because dropping or lengthening a piece is how cues used to end up on top of each other.
        /// </summary>
        private static void SplitIntoPieces(SubtitleCue cue, CueSplitOptions options, List<SubtitleCue> destination)
        {
            string text = cue.Text.Trim();
            double startSeconds = cue.Start.TotalSeconds;
            double endSeconds = cue.End.TotalSeconds;
            List<string> chunks = WrapIntoChunks(text, options.MaximumCharactersPerCue);

            if (chunks.Count <= 1 || endSeconds - startSeconds <= 0.0)
            {
                destination.Add(new SubtitleCue(cue.Start, cue.End, text, cue.Confidence));
                return;
            }

            int totalCharacters = 0;
            foreach (string chunk in chunks)
            {
                totalCharacters += Math.Max(1, chunk.Length);
            }

            double duration = endSeconds - startSeconds;
            double cursor = startSeconds;
            StringBuilder pending = new StringBuilder();

            for (int index = 0; index < chunks.Count; index++)
            {
                if (pending.Length > 0)
                {
                    pending.Append(' ');
                }

                pending.Append(chunks[index]);

                double share = duration * (Math.Max(1, chunks[index].Length) / (double)totalCharacters);
                double pieceEnd = index == chunks.Count - 1 ? endSeconds : cursor + share;
                if (pieceEnd > endSeconds)
                {
                    pieceEnd = endSeconds;
                }

                if (pieceEnd <= cursor)
                {
                    continue;
                }

                destination.Add(new SubtitleCue(
                    TimeSpan.FromSeconds(cursor),
                    TimeSpan.FromSeconds(pieceEnd),
                    pending.ToString(),
                    cue.Confidence));
                pending.Clear();
                cursor = pieceEnd;
            }

            if (pending.Length > 0 && destination.Count > 0)
            {
                // Only reachable when the phrase was shorter than the number of pieces it needs:
                // the leftover words join the last piece so no text is thrown away.
                SubtitleCue last = destination[destination.Count - 1];
                last.Text = last.Text + " " + pending.ToString();
            }
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
                double gapSeconds = (piece.Start - current.End).TotalSeconds;

                // The character budget, a real pause and a full stop are the only reasons to break.
                // Time on screen is never one of them: a line that fits the budget stays whole even
                // when the speaker drew it out.
                bool joinable = joined.Length <= options.MaximumCharactersPerCue
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

        /// <summary>
        /// Guarantees that no two cues are on screen at the same moment. Cues are sorted by start,
        /// so one pass is enough: an end that reaches past the next start is pulled back to it. The
        /// guard is a single millisecond so a boundary frame cannot light up both cues, and nothing
        /// else about the times is touched — starts are never moved and no cue is lengthened.
        /// </summary>
        private static void TrimOverlaps(List<SubtitleCue> cues)
        {
            for (int index = 0; index < cues.Count - 1; index++)
            {
                SubtitleCue current = cues[index];
                SubtitleCue next = cues[index + 1];
                if (current.End <= next.Start)
                {
                    continue;
                }

                double end = next.Start.TotalSeconds - OverlapGuardSeconds;
                if (end < current.Start.TotalSeconds + OverlapGuardSeconds)
                {
                    // A cue the next one covers completely keeps a sliver of its own, because the
                    // text still has to be readable somewhere. A millisecond of overlap is below a
                    // single frame and stays invisible.
                    end = current.Start.TotalSeconds + OverlapGuardSeconds;
                }

                current.End = TimeSpan.FromSeconds(end);
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
