using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SubtitleForge.Models;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Reads existing SRT / VTT files so a hand-made subtitle can be reopened and edited.
    /// </summary>
    public static class SubtitleReader
    {
        public static List<SubtitleCue> ReadFile(string path)
        {
            string content;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                content = reader.ReadToEnd();
            }

            return Parse(content);
        }

        public static List<SubtitleCue> Parse(string content)
        {
            List<SubtitleCue> cues = new List<SubtitleCue>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return cues;
            }

            string normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
            if (normalized.Length > 0 && normalized[0] == '\uFEFF')
            {
                normalized = normalized.Substring(1);
            }

            string[] lines = normalized.Split('\n');
            int index = 0;

            while (index < lines.Length)
            {
                string line = lines[index].Trim();

                if (line.Length == 0 || !line.Contains("-->"))
                {
                    index++;
                    continue;
                }

                int separator = line.IndexOf("-->", StringComparison.Ordinal);
                string startPart = line.Substring(0, separator).Trim();
                string endPart = line.Substring(separator + 3).Trim();

                // WebVTT may append cue settings after the end time.
                int settingsIndex = endPart.IndexOf(' ');
                if (settingsIndex > 0)
                {
                    endPart = endPart.Substring(0, settingsIndex).Trim();
                }

                TimeSpan start;
                TimeSpan end;
                if (!TryParseTime(startPart, out start) || !TryParseTime(endPart, out end))
                {
                    index++;
                    continue;
                }

                index++;
                StringBuilder text = new StringBuilder();
                while (index < lines.Length)
                {
                    string textLine = lines[index];
                    if (textLine.Trim().Length == 0)
                    {
                        break;
                    }

                    if (textLine.Contains("-->"))
                    {
                        break;
                    }

                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    text.Append(textLine.Trim());
                    index++;
                }

                string cueText = text.ToString().Trim();
                if (cueText.Length == 0)
                {
                    continue;
                }

                if (end <= start)
                {
                    end = start + TimeSpan.FromSeconds(1.0);
                }

                cues.Add(new SubtitleCue(start, end, cueText));
            }

            CuePostProcessor.Renumber(cues);
            return cues;
        }

        public static bool TryParseTime(string input, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            string candidate = input.Trim().Replace(',', '.');
            string[] parts = candidate.Split(':');
            if (parts.Length != 3)
            {
                return false;
            }

            int hours;
            int minutes;
            double seconds;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
            {
                return false;
            }

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes))
            {
                return false;
            }

            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            {
                return false;
            }

            value = TimeSpan.FromSeconds((hours * 3600.0) + (minutes * 60.0) + seconds);
            return value >= TimeSpan.Zero;
        }
    }
}
