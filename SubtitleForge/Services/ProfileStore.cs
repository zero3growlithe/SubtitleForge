using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubtitleForge.Models;
using Path = System.IO.Path;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Named visual profiles (style, idle animation, pop-in animation) kept as JSON under
    /// %AppData%\SubtitleForge\profiles. One file per kind so they can be shared by hand.
    /// </summary>
    public sealed class ProfileStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public List<StyleProfile> Styles { get; set; } = new List<StyleProfile>();

        public List<IdleAnimationProfile> Idle { get; set; } = new List<IdleAnimationProfile>();

        public List<PopInProfile> PopIns { get; set; } = new List<PopInProfile>();

        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SubtitleForge",
                    "profiles");
            }
        }

        public static string StylesPath
        {
            get { return Path.Combine(Folder, "styles.json"); }
        }

        public static string IdlePath
        {
            get { return Path.Combine(Folder, "idle.json"); }
        }

        public static string PopInsPath
        {
            get { return Path.Combine(Folder, "popin.json"); }
        }

        public static ProfileStore Load()
        {
            ProfileStore store = new ProfileStore();
            store.Styles = LoadList<StyleProfile>(StylesPath);
            store.Idle = LoadList<IdleAnimationProfile>(IdlePath);
            store.PopIns = LoadList<PopInProfile>(PopInsPath);

            // A styles.json written before outlines and shadows became one stack still has the two
            // old arrays; fold them into the single list so the rest of the program only ever sees
            // Effects.
            foreach (StyleProfile style in store.Styles)
            {
                style.Normalize();
            }

            store.EnsureDefaults();
            return store;
        }

        private static List<T> LoadList<T>(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new List<T>();
                }

                string json = File.ReadAllText(path);
                List<T>? loaded = JsonSerializer.Deserialize<List<T>>(json, Options);
                return loaded ?? new List<T>();
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "ProfileStore.Load:" + Path.GetFileName(path));
                return new List<T>();
            }
        }

        public void EnsureDefaults()
        {
            if (Styles.Count == 0)
            {
                Styles.Add(CreateDefaultStyle());
            }

            if (Idle.Count == 0)
            {
                Idle.Add(IdleAnimationProfile.CreateStill());
            }

            if (PopIns.Count == 0)
            {
                PopIns.Add(PopInProfile.CreateSoftFade());
            }
        }

        public static StyleProfile CreateDefaultStyle()
        {
            StyleProfile style = new StyleProfile();
            style.Name = "Default";
            style.FontFamily = "Segoe UI";
            style.Bold = true;
            style.FontSize = 64.0;
            style.Fill = ColorSpec.Solid("#FFFFFFFF");
            style.Effects = EffectLayer.CreateDefaults();
            style.WordsPerDisplay = 0;
            style.HighlightSpokenWord = false;
            style.HighlightFill = ColorSpec.Solid("#FFFFD54A");
            return style;
        }

        public void Save()
        {
            SaveList(StylesPath, Styles);
            SaveList(IdlePath, Idle);
            SaveList(PopInsPath, PopIns);
        }

        private static void SaveList<T>(string path, List<T> items)
        {
            try
            {
                string? folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(items, Options),
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "ProfileStore.Save:" + Path.GetFileName(path));
            }
        }

        public StyleProfile ResolveStyle(string? id)
        {
            foreach (StyleProfile style in Styles)
            {
                if (string.Equals(style.Id, id, StringComparison.Ordinal))
                {
                    return style;
                }
            }

            return Styles[0];
        }

        public IdleAnimationProfile ResolveIdle(string? id)
        {
            foreach (IdleAnimationProfile profile in Idle)
            {
                if (string.Equals(profile.Id, id, StringComparison.Ordinal))
                {
                    return profile;
                }
            }

            return Idle[0];
        }

        public PopInProfile ResolvePopIn(string? id)
        {
            foreach (PopInProfile profile in PopIns)
            {
                if (string.Equals(profile.Id, id, StringComparison.Ordinal))
                {
                    return profile;
                }
            }

            return PopIns[0];
        }

        /// <summary>
        /// Copies every setting of one style profile onto another, keeping the target's id and name.
        /// Cues and settings reference profiles by id, so the copy has to stay in place instead of
        /// being added under a new identity.
        /// </summary>
        public bool CopyStyleSettings(string sourceId, string targetId)
        {
            if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
            {
                return false;
            }

            int targetIndex = IndexOfStyle(targetId);
            StyleProfile? source = FindStyle(sourceId);
            if (targetIndex < 0 || source == null)
            {
                return false;
            }

            StyleProfile target = Styles[targetIndex];
            StyleProfile copy = source.Clone();
            copy.Id = target.Id;
            copy.Name = target.Name;
            Styles[targetIndex] = copy;
            Save();
            return true;
        }

        /// <summary>Copies every setting of one idle profile onto another, keeping the target's id.</summary>
        public bool CopyIdleSettings(string sourceId, string targetId)
        {
            if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
            {
                return false;
            }

            IdleAnimationProfile? source = FindIdle(sourceId);
            if (source == null)
            {
                return false;
            }

            for (int index = 0; index < Idle.Count; index++)
            {
                if (!string.Equals(Idle[index].Id, targetId, StringComparison.Ordinal))
                {
                    continue;
                }

                IdleAnimationProfile copy = source.Clone();
                copy.Id = Idle[index].Id;
                copy.Name = Idle[index].Name;
                Idle[index] = copy;
                Save();
                return true;
            }

            return false;
        }

        /// <summary>Copies every setting of one pop-in profile onto another, keeping the target's id.</summary>
        public bool CopyPopInSettings(string sourceId, string targetId)
        {
            if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
            {
                return false;
            }

            PopInProfile? source = FindPopIn(sourceId);
            if (source == null)
            {
                return false;
            }

            for (int index = 0; index < PopIns.Count; index++)
            {
                if (!string.Equals(PopIns[index].Id, targetId, StringComparison.Ordinal))
                {
                    continue;
                }

                PopInProfile copy = source.Clone();
                copy.Id = PopIns[index].Id;
                copy.Name = PopIns[index].Name;
                PopIns[index] = copy;
                Save();
                return true;
            }

            return false;
        }

        private StyleProfile? FindStyle(string id)
        {
            int index = IndexOfStyle(id);
            return index < 0 ? null : Styles[index];
        }

        private int IndexOfStyle(string id)
        {
            for (int index = 0; index < Styles.Count; index++)
            {
                if (string.Equals(Styles[index].Id, id, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        private IdleAnimationProfile? FindIdle(string id)
        {
            foreach (IdleAnimationProfile profile in Idle)
            {
                if (string.Equals(profile.Id, id, StringComparison.Ordinal))
                {
                    return profile;
                }
            }

            return null;
        }

        private PopInProfile? FindPopIn(string id)
        {
            foreach (PopInProfile profile in PopIns)
            {
                if (string.Equals(profile.Id, id, StringComparison.Ordinal))
                {
                    return profile;
                }
            }

            return null;
        }

        public void UpsertStyle(StyleProfile profile)
        {
            for (int index = 0; index < Styles.Count; index++)
            {
                if (string.Equals(Styles[index].Id, profile.Id, StringComparison.Ordinal))
                {
                    Styles[index] = profile;
                    return;
                }
            }

            Styles.Add(profile);
        }

        public void UpsertIdle(IdleAnimationProfile profile)
        {
            for (int index = 0; index < Idle.Count; index++)
            {
                if (string.Equals(Idle[index].Id, profile.Id, StringComparison.Ordinal))
                {
                    Idle[index] = profile;
                    return;
                }
            }

            Idle.Add(profile);
        }

        public void UpsertPopIn(PopInProfile profile)
        {
            for (int index = 0; index < PopIns.Count; index++)
            {
                if (string.Equals(PopIns[index].Id, profile.Id, StringComparison.Ordinal))
                {
                    PopIns[index] = profile;
                    return;
                }
            }

            PopIns.Add(profile);
        }
    }
}
