namespace Jarvis.Core.Sound;

/// <summary>Interface sounds, bound from the "Sounds" settings section.</summary>
public sealed class SoundOptions
{
    public const string SectionName = "Sounds";

    public bool Enabled { get; set; } = true;

    /// <summary>The pen sound while Jarvis draws on the screen.</summary>
    public bool DrawingSounds { get; set; } = true;
}
