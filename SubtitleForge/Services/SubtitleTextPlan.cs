using System;
using System.Collections.Generic;
using System.Globalization;

namespace SubtitleForge.Services
{
    /// <summary>One word of a cue and when it is being spoken (proportional timing).</summary>
    public sealed class WordTiming
    {
        public string Text { get; set; } = string.Empty;

        public int CharacterStart { get; set; }

        public int CharacterLength { get; set; }

        public double StartSeconds { get; set; }

        public double EndSeconds { get; set; }
    }

    /// <summary>
    /// Splits a cue into words and distributes the cue duration over them by length, so the
    /// renderer knows which word is being spoken without word-level timestamps from Whisper.
    /// </summary>
    public static class SubtitleTextPlan
    {
        public static List<WordTiming> BuildWords(string? text, double cueStartSeconds, double cueDurationSeconds)
        {
            List<WordTiming> words = new List<WordTiming>();
            if (string.IsNullOrEmpty(text))
            {
                return words;
            }

            int index = 0;
            int length = text.Length;

            while (index < length)
            {
                while (index < length && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                if (index >= length)
                {
                    break;
                }

                int start = index;
                while (index < length && !char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                WordTiming word = new WordTiming();
                word.Text = text.Substring(start, index - start);
                word.CharacterStart = start;
                word.CharacterLength = index - start;
                words.Add(word);
            }

            if (words.Count == 0)
            {
                return words;
            }

            double totalWeight = 0.0;
            foreach (WordTiming word in words)
            {
                totalWeight += word.CharacterLength + 1.0;
            }

            if (totalWeight <= 0.0)
            {
                totalWeight = words.Count;
            }

            double cursor = cueStartSeconds;
            double duration = cueDurationSeconds > 0.0 ? cueDurationSeconds : 0.0;

            for (int wordIndex = 0; wordIndex < words.Count; wordIndex++)
            {
                WordTiming word = words[wordIndex];
                double weight = (word.CharacterLength + 1.0) / totalWeight;
                word.StartSeconds = cursor;
                word.EndSeconds = cursor + (duration * weight);
                cursor = word.EndSeconds;
            }

            return words;
        }

        /// <summary>Index of the word being spoken at the given absolute time (-1 before the first).</summary>
        public static int FindWordIndexAt(List<WordTiming> words, double positionSeconds)
        {
            if (words == null || words.Count == 0)
            {
                return -1;
            }

            for (int index = 0; index < words.Count; index++)
            {
                if (positionSeconds < words[index].EndSeconds)
                {
                    return index;
                }
            }

            return words.Count - 1;
        }

        /// <summary>Words visible at the same time when showing N words at a time.</summary>
        public static List<WordTiming> WindowWords(List<WordTiming> words, int activeIndex, int wordsPerDisplay)
        {
            List<WordTiming> window = new List<WordTiming>();
            if (words == null || words.Count == 0)
            {
                return window;
            }

            if (wordsPerDisplay <= 0 || wordsPerDisplay >= words.Count)
            {
                window.AddRange(words);
                return window;
            }

            int clamped = activeIndex < 0 ? 0 : activeIndex;
            int windowStart = (clamped / wordsPerDisplay) * wordsPerDisplay;
            int windowEnd = Math.Min(words.Count, windowStart + wordsPerDisplay);

            for (int index = windowStart; index < windowEnd; index++)
            {
                window.Add(words[index]);
            }

            return window;
        }

        public static string JoinWords(List<WordTiming> words)
        {
            if (words == null || words.Count == 0)
            {
                return string.Empty;
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int index = 0; index < words.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(words[index].Text);
            }

            return builder.ToString();
        }

        /// <summary>Progress 0-1 through the visible words, used for character appearance times.</summary>
        public static double WordProgress(WordTiming word, double positionSeconds)
        {
            double span = word.EndSeconds - word.StartSeconds;
            if (span <= 0.0001)
            {
                return positionSeconds >= word.StartSeconds ? 1.0 : 0.0;
            }

            double value = (positionSeconds - word.StartSeconds) / span;
            if (value < 0.0)
            {
                return 0.0;
            }

            return value > 1.0 ? 1.0 : value;
        }

        public static string FormatSeconds(double seconds)
        {
            return seconds.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
