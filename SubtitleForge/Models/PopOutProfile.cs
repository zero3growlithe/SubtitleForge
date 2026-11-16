using System;

namespace SubtitleForge.Models
{
    /// <summary>How a cue leaves the screen.</summary>
    public static class PopOutKind
    {
        public const string None = "None";

        /// <summary>Opacity drops to nothing.</summary>
        public const string FadeOut = "FadeOut";

        /// <summary>The text travels out of frame in one direction.</summary>
        public const string SlideOut = "SlideOut";

        /// <summary>The glyphs shrink away or blow up past the frame, with the same jelly as the entrance.</summary>
        public const string ScaleOut = "ScaleOut";

        /// <summary>Rotation around X, Y (faked by squashing) or Z.</summary>
        public const string SpinOut = "SpinOut";

        /// <summary>The glyph breaks apart in noise-shaped patches until nothing is left.</summary>
        public const string Dissolve = "Dissolve";

        /// <summary>The glyph crumbles into pieces that are blown away by wind.</summary>
        public const string Dust = "Dust";
    }

    /// <summary>Axis a spin-out turns around. X and Y are faked on a 2D canvas by squashing the opposite axis.</summary>
    public static class PopOutAxis
    {
        public const string X = "X";
        public const string Y = "Y";
        public const string Z = "Z";
    }

    /// <summary>
    /// Where a dissolve or a crumble starts. The names are the gradient directions, so a direction
    /// here means what it means in a fill: LeftToRight starts on the left edge and sweeps right.
    /// </summary>
    public static class PopOutOrigin
    {
        public const string LeftToRight = "LeftToRight";
        public const string RightToLeft = "RightToLeft";
        public const string TopToBottom = "TopToBottom";
        public const string BottomToTop = "BottomToTop";
        public const string DownRight = "DownRight";
        public const string UpRight = "UpRight";
        public const string DownLeft = "DownLeft";
        public const string UpLeft = "UpLeft";
        public const string Center = "Center";
        public const string Random = "Random";
    }

    /// <summary>
    /// How a cue disappears. The exit is timed against the end of the cue: it finishes exactly when
    /// the cue ends, so nothing lingers on screen after a subtitle's own end time — the stagger and
    /// the duration are compressed when the cue is too short to hold them.
    /// </summary>
    public sealed class PopOutProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "Pop-out";

        public string Kind { get; set; } = PopOutKind.FadeOut;

        /// <summary>Length of the exit in seconds.</summary>
        public double DurationSeconds { get; set; } = 0.30;

        /// <summary>Staggers the exit across characters or words, exactly like the entrance does.</summary>
        public bool PerCharacter { get; set; }

        /// <summary>What the stagger walks over: every character, whole words, or the whole cue.</summary>
        public string StaggerGrouping { get; set; } = StaggerGroupings.Character;

        /// <summary>Delay between neighbouring groups in seconds.</summary>
        public double CharacterDelaySeconds { get; set; } = 0.03;

        /// <summary>
        /// Shape of the exit: 1 is linear, 2 accelerates towards the end, 0.5 leaves quickly and
        /// hangs. Applies to every kind.
        /// </summary>
        public double EasePower { get; set; } = 1.8;

        // ── Slide out ──────────────────────────────────────────────────────

        /// <summary>Slide direction, the same four values the entrance uses.</summary>
        public string Direction { get; set; } = PopInDirection.Bottom;

        /// <summary>Travel distance as a multiple of the font size.</summary>
        public double DistanceMultiplier { get; set; } = 1.2;

        // ── Scale out ──────────────────────────────────────────────────────

        /// <summary>Scale the glyph ends at. 0 shrinks it away, values above 1 blow it up past the frame.</summary>
        public double EndScale { get; set; }

        public double JellyAmplitudeX { get; set; } = 0.30;

        public double JellyAmplitudeY { get; set; } = 0.30;

        public double JellyFrequency { get; set; } = 2.0;

        public double JellyFalloff { get; set; } = 2.5;

        // ── Spin out ───────────────────────────────────────────────────────

        public string SpinAxis { get; set; } = PopOutAxis.Y;

        /// <summary>Turns over the exit. 0.5 is half a revolution; a negative value spins the other way.</summary>
        public double SpinTurns { get; set; } = 0.5;

        /// <summary>Extra scale applied while spinning, so the glyph can also fall away.</summary>
        public double SpinEndScale { get; set; } = 1.0;

        // ── Dissolve and dust ──────────────────────────────────────────────

        /// <summary>Cells across the glyph: the size of the pieces it breaks into.</summary>
        public int CellCount { get; set; } = 10;

        /// <summary>How much the noise distorts the sweep. 0 is a clean sweep, 1 is pure noise.</summary>
        public double NoiseAmount { get; set; } = 0.45;

        public int Seed { get; set; } = 7;

        /// <summary>Where the crumble starts.</summary>
        public string Origin { get; set; } = PopOutOrigin.LeftToRight;

        /// <summary>Pieces that are blown away, per glyph (dust only).</summary>
        public int ParticleCount { get; set; } = 14;

        /// <summary>Particle size as a multiple of the font size.</summary>
        public double ParticleSize { get; set; } = 0.045;

        /// <summary>Wind direction in degrees: 0 blows right, 90 blows down, 180 left, 270 up.</summary>
        public double WindAngleDegrees { get; set; } = 90.0;

        /// <summary>How far the wind carries a piece, as a multiple of the font size.</summary>
        public double WindForce { get; set; } = 1.4;

        /// <summary>Random spread of the wind in degrees, so the pieces do not travel in one beam.</summary>
        public double WindSpread { get; set; } = 40.0;

        /// <summary>Spin of a single piece over its flight, in degrees.</summary>
        public double ParticleSpinDegrees { get; set; } = 180.0;

        /// <summary>True when this profile actually changes anything.</summary>
        public bool HasExit
        {
            get { return !string.Equals(Kind, PopOutKind.None, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>True when the exit eats into the glyph instead of only fading it.</summary>
        public bool BreaksPieces
        {
            get
            {
                return string.Equals(Kind, PopOutKind.Dissolve, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Kind, PopOutKind.Dust, StringComparison.OrdinalIgnoreCase);
            }
        }

        public PopOutProfile Clone()
        {
            PopOutProfile copy = new PopOutProfile();
            copy.Id = Id;
            copy.Name = Name;
            copy.Kind = Kind;
            copy.DurationSeconds = DurationSeconds;
            copy.PerCharacter = PerCharacter;
            copy.StaggerGrouping = StaggerGrouping;
            copy.CharacterDelaySeconds = CharacterDelaySeconds;
            copy.EasePower = EasePower;
            copy.Direction = Direction;
            copy.DistanceMultiplier = DistanceMultiplier;
            copy.EndScale = EndScale;
            copy.JellyAmplitudeX = JellyAmplitudeX;
            copy.JellyAmplitudeY = JellyAmplitudeY;
            copy.JellyFrequency = JellyFrequency;
            copy.JellyFalloff = JellyFalloff;
            copy.SpinAxis = SpinAxis;
            copy.SpinTurns = SpinTurns;
            copy.SpinEndScale = SpinEndScale;
            copy.CellCount = CellCount;
            copy.NoiseAmount = NoiseAmount;
            copy.Seed = Seed;
            copy.Origin = Origin;
            copy.ParticleCount = ParticleCount;
            copy.ParticleSize = ParticleSize;
            copy.WindAngleDegrees = WindAngleDegrees;
            copy.WindForce = WindForce;
            copy.WindSpread = WindSpread;
            copy.ParticleSpinDegrees = ParticleSpinDegrees;
            return copy;
        }

        /// <summary>
        /// Everything that decides how a glyph breaks apart. The overlay caches the pieces per glyph
        /// and rebuilds them when this signature changes.
        /// </summary>
        public string ShapeSignature()
        {
            return string.Concat(
                Kind, "|",
                CellCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
                NoiseAmount.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), "|",
                Seed.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
                Origin, "|",
                ParticleCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
                ParticleSize.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        }

        public static PopOutProfile CreateFade()
        {
            PopOutProfile profile = new PopOutProfile();
            profile.Name = "Soft fade out";
            profile.Kind = PopOutKind.FadeOut;
            profile.DurationSeconds = 0.25;
            return profile;
        }

        public static PopOutProfile CreateDust()
        {
            PopOutProfile profile = new PopOutProfile();
            profile.Name = "Turn to dust";
            profile.Kind = PopOutKind.Dust;
            profile.DurationSeconds = 0.55;
            profile.PerCharacter = true;
            profile.CharacterDelaySeconds = 0.02;
            profile.CellCount = 8;
            profile.NoiseAmount = 0.5;
            profile.ParticleCount = 16;
            profile.WindAngleDegrees = 100.0;
            profile.WindForce = 1.6;
            profile.WindSpread = 55.0;
            return profile;
        }
    }
}
