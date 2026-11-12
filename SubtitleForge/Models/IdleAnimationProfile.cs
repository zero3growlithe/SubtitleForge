using System;
using System.Collections.Generic;

namespace SubtitleForge.Models
{
    /// <summary>What an animation staggers across when it is not applied to the whole cue.</summary>
    public static class StaggerGroupings
    {
        /// <summary>Every character is offset from the previous one.</summary>
        public const string Character = "Character";

        /// <summary>All characters of a word move together; words are offset from each other.</summary>
        public const string Word = "Word";

        /// <summary>The whole cue animates as one block.</summary>
        public const string Cue = "Cue";
    }

    /// <summary>
    /// One animation channel. The meaning of Amplitude depends on the channel:
    /// sine uses a multiple of the font size, shake a multiple of the font size, pulse a
    /// scale delta (0.1 = +-10%), wiggle degrees and spin rotations per second.
    /// </summary>
    public sealed class IdleChannel
    {
        public bool Enabled { get; set; }

        public double Amplitude { get; set; }

        /// <summary>Cycles per second.</summary>
        public double Frequency { get; set; } = 1.0;

        /// <summary>Phase offset in degrees.</summary>
        public double Phase { get; set; }

        public IdleChannel Clone()
        {
            IdleChannel copy = new IdleChannel();
            copy.Enabled = Enabled;
            copy.Amplitude = Amplitude;
            copy.Frequency = Frequency;
            copy.Phase = Phase;
            return copy;
        }
    }

    /// <summary>Constant motion applied to the text: shake, sine drift, pulse, wiggle, spin.</summary>
    public sealed class IdleAnimationProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "Idle";

        /// <summary>True animates every glyph on its own (a travelling wave), false the whole text.</summary>
        public bool PerCharacter { get; set; }

        /// <summary>Stagger between neighbouring characters in seconds (per-character mode).</summary>
        public double CharacterStaggerSeconds { get; set; } = 0.06;

        /// <summary>
        /// What the stagger walks over: <see cref="StaggerGroupings.Character"/> gives a travelling
        /// wave, <see cref="StaggerGroupings.Word"/> moves whole words out of phase with each other
        /// and <see cref="StaggerGroupings.Cue"/> animates the text as one block.
        /// </summary>
        public string StaggerGrouping { get; set; } = StaggerGroupings.Character;

        public IdleChannel SineX { get; set; } = new IdleChannel();

        public IdleChannel SineY { get; set; } = new IdleChannel();

        public IdleChannel Shake { get; set; } = new IdleChannel();

        public IdleChannel Pulse { get; set; } = new IdleChannel();

        public IdleChannel Wiggle { get; set; } = new IdleChannel();

        /// <summary>Spins around the vertical axis, faked by squashing the width.</summary>
        public IdleChannel Spin { get; set; } = new IdleChannel();

        public bool HasAnyChannel
        {
            get
            {
                return SineX.Enabled || SineY.Enabled || Shake.Enabled
                    || Pulse.Enabled || Wiggle.Enabled || Spin.Enabled;
            }
        }

        public IdleAnimationProfile Clone()
        {
            IdleAnimationProfile copy = new IdleAnimationProfile();
            copy.Id = Id;
            copy.Name = Name;
            copy.PerCharacter = PerCharacter;
            copy.CharacterStaggerSeconds = CharacterStaggerSeconds;
            copy.StaggerGrouping = StaggerGrouping;
            copy.SineX = SineX.Clone();
            copy.SineY = SineY.Clone();
            copy.Shake = Shake.Clone();
            copy.Pulse = Pulse.Clone();
            copy.Wiggle = Wiggle.Clone();
            copy.Spin = Spin.Clone();
            return copy;
        }

        public List<IdleChannel> EnumerateChannels()
        {
            List<IdleChannel> channels = new List<IdleChannel>();
            channels.Add(Shake);
            channels.Add(SineX);
            channels.Add(SineY);
            channels.Add(Pulse);
            channels.Add(Wiggle);
            channels.Add(Spin);
            return channels;
        }

        public static IdleAnimationProfile CreateStill()
        {
            IdleAnimationProfile profile = new IdleAnimationProfile();
            profile.Name = "Still";
            return profile;
        }
    }
}
