using System;

namespace SubtitleForge.Models
{
    public static class PopInKind
    {
        public const string None = "None";
        public const string SlideIn = "SlideIn";
        public const string ScaleIn = "ScaleIn";
        public const string FadeIn = "FadeIn";
    }

    public static class PopInDirection
    {
        public const string Top = "Top";
        public const string Bottom = "Bottom";
        public const string Left = "Left";
        public const string Right = "Right";
    }

    /// <summary>How a new cue enters the screen.</summary>
    public sealed class PopInProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "Pop-in";

        public string Kind { get; set; } = PopInKind.FadeIn;

        /// <summary>Slide direction (slide mode).</summary>
        public string Direction { get; set; } = PopInDirection.Bottom;

        /// <summary>Travel distance as a multiple of the font size.</summary>
        public double DistanceMultiplier { get; set; } = 0.8;

        /// <summary>Spring stiffness: how fast it snaps into place.</summary>
        public double SpringStiffness { get; set; } = 8.0;

        /// <summary>Spring damping: lower bounces more.</summary>
        public double SpringDamping { get; set; } = 0.55;

        public double JellyAmplitudeX { get; set; } = 0.35;

        public double JellyAmplitudeY { get; set; } = 0.35;

        public double JellyFrequency { get; set; } = 2.5;

        public double JellyFalloff { get; set; } = 3.0;

        /// <summary>Total length of the entrance in seconds.</summary>
        public double DurationSeconds { get; set; } = 0.35;

        public bool PerCharacter { get; set; }

        /// <summary>Delay between neighbouring characters in per-character mode.</summary>
        public double CharacterDelaySeconds { get; set; } = 0.045;

        /// <summary>
        /// What the delay walks over when <see cref="PerCharacter"/> is on: every character, whole words
        /// out of phase, or a single block for the entire cue.
        /// </summary>
        public string StaggerGrouping { get; set; } = StaggerGroupings.Character;

        public PopInProfile Clone()
        {
            PopInProfile copy = new PopInProfile();
            copy.Id = Id;
            copy.Name = Name;
            copy.Kind = Kind;
            copy.Direction = Direction;
            copy.DistanceMultiplier = DistanceMultiplier;
            copy.SpringStiffness = SpringStiffness;
            copy.SpringDamping = SpringDamping;
            copy.JellyAmplitudeX = JellyAmplitudeX;
            copy.JellyAmplitudeY = JellyAmplitudeY;
            copy.JellyFrequency = JellyFrequency;
            copy.JellyFalloff = JellyFalloff;
            copy.DurationSeconds = DurationSeconds;
            copy.PerCharacter = PerCharacter;
            copy.CharacterDelaySeconds = CharacterDelaySeconds;
            copy.StaggerGrouping = StaggerGrouping;
            return copy;
        }

        public static PopInProfile CreateSoftFade()
        {
            PopInProfile profile = new PopInProfile();
            profile.Name = "Soft fade";
            profile.Kind = PopInKind.FadeIn;
            profile.DurationSeconds = 0.25;
            return profile;
        }
    }
}
