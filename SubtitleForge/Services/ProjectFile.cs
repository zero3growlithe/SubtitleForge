using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubtitleForge.Services
{
    public class ProjectSegmentData
    {
        public double StartSeconds { get; set; }

        public double SourceOffsetSeconds { get; set; }

        public double DurationSeconds { get; set; }

        public double SourceDuration { get; set; }

        /// <summary>
        /// Absolute path (or project-relative path) of the media this segment reads from.
        /// </summary>
        public string? SourcePath { get; set; }

        /// <summary>
        /// Project-relative file name of an exported clip (recorded audio).
        /// </summary>
        public string? ClipFile { get; set; }

        public bool HasRecordedAudio { get; set; }
    }

    public class ProjectTrackData
    {
        public string Kind { get; set; } = "Audio";

        public string Name { get; set; } = string.Empty;

        public string BackgroundColor { get; set; } = "#FF2B3A55";

        public List<ProjectSegmentData> Segments { get; set; } = new List<ProjectSegmentData>();
    }

    public class ProjectCueData
    {
        public double StartSeconds { get; set; }

        public double EndSeconds { get; set; }

        public string Text { get; set; } = string.Empty;

        public double Confidence { get; set; } = 1.0;

        public string? StyleProfileId { get; set; }

        public string? IdleProfileId { get; set; }

        public string? PopInProfileId { get; set; }

        public double? PositionX { get; set; }

        public double? PositionY { get; set; }

        public double? Scale { get; set; }
    }

    public class ProjectFileData
    {
        public string Format { get; set; } = "SubtitleForge.Project";

        public int Version { get; set; } = 1;

        /// <summary>
        /// The video the timeline is built around — a reference, never a copy.
        /// </summary>
        public string VideoPath { get; set; } = string.Empty;

        public double DurationSeconds { get; set; }

        public double FrameStepSeconds { get; set; } = 1.0 / 30.0;

        public List<ProjectTrackData> Tracks { get; set; } = new List<ProjectTrackData>();

        public List<ProjectCueData> Cues { get; set; } = new List<ProjectCueData>();
    }

    /// <summary>
    /// Reads and writes the custom .sfproj project format: JSON plus side-car WAV clips that
    /// hold recorded audio, so the project file itself stays tiny and every media path is a
    /// reference rather than a copy.
    /// </summary>
    public static class ProjectFile
    {
        public const string Extension = ".sfproj";

        public const string FilterText = "SubtitleForge project (*.sfproj)|*.sfproj";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static void Save(string path, ProjectFileData data)
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            string json = JsonSerializer.Serialize(data, Options);
            File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
        }

        public static ProjectFileData Load(string path)
        {
            string json = File.ReadAllText(path);
            ProjectFileData? data = JsonSerializer.Deserialize<ProjectFileData>(json);
            if (data == null || string.IsNullOrWhiteSpace(data.Format))
            {
                throw new InvalidDataException("This file is not a SubtitleForge project.");
            }

            if (!string.Equals(data.Format, "SubtitleForge.Project", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unsupported project format: " + data.Format);
            }

            return data;
        }

        /// <summary>
        /// Resolves a path stored in the project: relative entries are relative to the
        /// project file, absolute ones are returned untouched.
        /// </summary>
        public static string ResolveRelativePath(string projectPath, string? entry)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(entry))
            {
                return entry;
            }

            string? folder = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(folder))
            {
                return entry;
            }

            return Path.GetFullPath(Path.Combine(folder, entry));
        }

        /// <summary>
        /// Builds the clip file name used for a recorded segment: "&lt;project&gt;-track&lt;n&gt;-clip&lt;m&gt;.wav".
        /// </summary>
        public static string BuildClipFileName(string projectPath, int trackIndex, int segmentIndex)
        {
            string baseName = Path.GetFileNameWithoutExtension(projectPath);
            return baseName
                + "-track"
                + (trackIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "-clip"
                + (segmentIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ".wav";
        }
    }
}
