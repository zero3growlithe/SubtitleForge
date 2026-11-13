using System;
using System.IO;
using System.Text.Json;

namespace SubtitleForge.Services
{
    /// <summary>
    /// User preferences, stored as JSON under %AppData%\SubtitleForge\settings.json.
    /// </summary>
    public class AppSettings
    {
        public string ModelFileName { get; set; } = "ggml-small.bin";

        public string Language { get; set; } = "auto";

        public bool TranslateToEnglish { get; set; } = false;

        public bool UseGpu { get; set; } = true;

        public int Threads { get; set; } = Math.Max(2, Math.Min(8, Environment.ProcessorCount - 1));

        public bool SkipNonSpeech { get; set; } = true;

        public int MaximumCharactersPerCue { get; set; } = 48;

        public double MaximumCueDurationSeconds { get; set; } = 7.0;

        public double MinimumCueDurationSeconds { get; set; } = 0.6;

        public double MinimumGapSeconds { get; set; } = 0.04;

        public string? FfmpegDirectory { get; set; }

        public string FontName { get; set; } = "Segoe UI";

        public int FontSize { get; set; } = 20;

        public int MarginVertical { get; set; } = 40;

        public int OutlineThickness { get; set; } = 1;

        /// <summary>Style profile used by every cue that does not override it.</summary>
        public string? DefaultStyleProfileId { get; set; }

        /// <summary>Idle animation profile used by every cue that does not override it.</summary>
        public string? DefaultIdleProfileId { get; set; }

        /// <summary>Pop-in animation profile used by every cue that does not override it.</summary>
        public string? DefaultPopInProfileId { get; set; }

        /// <summary>Horizontal anchor of the subtitle text centre, 0-1 of the frame.</summary>
        public double OverlayPositionX { get; set; } = 0.5;

        /// <summary>Vertical anchor of the subtitle text centre, 0-1 of the frame.</summary>
        public double OverlayPositionY { get; set; } = 0.86;

        /// <summary>Extra scale applied on top of the style's font size.</summary>
        public double OverlayScale { get; set; } = 1.0;

        /// <summary>
        /// Show every cue that is on screen at the same time, stacked the way the burn-in export
        /// draws them, instead of only the one the playhead entered last.
        /// </summary>
        public bool ShowOverlappingCues { get; set; } = true;

        /// <summary>
        /// When true, moving the playhead selects every cue that is on screen at that position, so the
        /// cue panel always edits what the preview shows. Turning it off keeps the selection untouched
        /// while scrubbing.
        /// </summary>
        public bool SelectCuesAtPlayhead { get; set; } = true;

        public string BurnPreset { get; set; } = "medium";

        public int BurnCrf { get; set; } = 20;

        /// <summary>
        /// Retired: the global time offset field was removed from the cue panel, so this always
        /// reads zero. Kept so old settings files still deserialise, and forced back to zero on
        /// load so a value saved earlier cannot silently shift every subtitle with no way to fix it.
        /// </summary>
        public double SubtitleOffsetSeconds { get; set; }

        /// <summary>Height of the multitrack timeline panel, remembered from the splitter.</summary>
        public double TimelineHeight { get; set; } = 204.0;

        /// <summary>Width of the cue panel on the right, remembered from the splitter.</summary>
        public double CuePanelWidth { get; set; } = 474.0;

        /// <summary>Restores the window maximised on the next launch.</summary>
        public bool WindowMaximized { get; set; }

        public string? LastDirectory { get; set; }

        public static string SettingsPath
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SubtitleForge");
                return Path.Combine(folder, "settings.json");
            }
        }

        public static AppSettings Load()
        {
            try
            {
                string path = SettingsPath;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null)
                    {
                        loaded.Sanitize();
                        return loaded;
                    }
                }
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "AppSettings.Load");
            }

            AppSettings settings = new AppSettings();
            settings.Sanitize();
            return settings;
        }

        public void Save()
        {
            try
            {
                Sanitize();
                string path = SettingsPath;
                string? folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, JsonSerializer.Serialize(this, options));
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "AppSettings.Save");
            }
        }

        private void Sanitize()
        {
            // No UI sets this any more; a stale value from an older settings file would move every
            // cue with nothing on screen to explain it.
            SubtitleOffsetSeconds = 0.0;

            if (string.IsNullOrWhiteSpace(ModelFileName))
            {
                ModelFileName = "ggml-small.bin";
            }

            if (string.IsNullOrWhiteSpace(Language))
            {
                Language = "auto";
            }

            if (Threads < 1)
            {
                Threads = 1;
            }

            if (Threads > 16)
            {
                Threads = 16;
            }

            if (MaximumCharactersPerCue < 16)
            {
                MaximumCharactersPerCue = 16;
            }

            if (MaximumCharactersPerCue > 200)
            {
                MaximumCharactersPerCue = 200;
            }

            if (MaximumCueDurationSeconds < 1.0)
            {
                MaximumCueDurationSeconds = 1.0;
            }

            if (MaximumCueDurationSeconds > 30.0)
            {
                MaximumCueDurationSeconds = 30.0;
            }

            if (MinimumCueDurationSeconds < 0.2)
            {
                MinimumCueDurationSeconds = 0.2;
            }

            if (MinimumCueDurationSeconds > MaximumCueDurationSeconds)
            {
                MinimumCueDurationSeconds = MaximumCueDurationSeconds;
            }

            if (MinimumGapSeconds < 0.0)
            {
                MinimumGapSeconds = 0.0;
            }

            if (FontSize < 8)
            {
                FontSize = 8;
            }

            if (FontSize > 120)
            {
                FontSize = 120;
            }

            if (MarginVertical < 0)
            {
                MarginVertical = 0;
            }

            if (OutlineThickness < 0)
            {
                OutlineThickness = 0;
            }

            if (BurnCrf < 0)
            {
                BurnCrf = 0;
            }

            if (BurnCrf > 51)
            {
                BurnCrf = 51;
            }

            if (string.IsNullOrWhiteSpace(BurnPreset))
            {
                BurnPreset = "medium";
            }

            if (string.IsNullOrWhiteSpace(FontName))
            {
                FontName = "Segoe UI";
            }
        }
    }
}
