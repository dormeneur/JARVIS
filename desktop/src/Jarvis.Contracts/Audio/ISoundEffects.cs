namespace Jarvis.Contracts.Audio;

/// <summary>Short interface sounds. Fire and forget; safe from any thread.</summary>
public interface ISoundEffects
{
    void Play(SoundEffect effect);
}

public enum SoundEffect
{
    ComposerOpen,
    ComposerSend,
    ComposerClose,
    JobStarted,
    JobDone,
    JobFailed,

    /// <summary>A typed question's answer has started.</summary>
    AnswerReady,

    /// <summary>A soft pen scratch while Jarvis draws on the screen.</summary>
    Drawing,

    /// <summary>The microphone opened (push-to-talk held).</summary>
    ListenStart,

    /// <summary>The microphone closed: what was said is on its way.</summary>
    ListenEnd,
}
