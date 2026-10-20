namespace StormATC.Application.Abstractions;

/// <summary>What happened when the pilot asked to set the last voice aside.</summary>
public enum VoiceRejectionOutcome
{
    /// <summary>The voice was added to the excluded voices; the controller gets another one next time.</summary>
    SetAside,

    /// <summary>No local voice has spoken yet.</summary>
    NoVoiceYet,

    /// <summary>The voice is the one chosen in Settings, which is changed there rather than set aside.</summary>
    ChosenInSettings,

    /// <summary>The settings file could not be written.</summary>
    Failed,
}

/// <summary>The result of <see cref="IVoiceRejection.RejectLastVoiceAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Voice">A short description of the voice concerned, or empty.</param>
public sealed record VoiceRejectionResult(VoiceRejectionOutcome Outcome, string Voice);

/// <summary>
/// Sets aside the controller voice that spoke last, from the dashboard or from the in-simulator
/// panel: a pilot who dislikes a voice says so without leaving the cockpit.
/// </summary>
public interface IVoiceRejection
{
    /// <summary>The voice that spoke last, or null when none has or the engine has no choice of voice.</summary>
    SpokenVoice? LastVoice { get; }

    /// <summary>
    /// Adds the last voice to <c>PiperExcludedVoices</c>. The controller that had it is given
    /// another voice the next time it speaks.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VoiceRejectionResult> RejectLastVoiceAsync(CancellationToken cancellationToken);
}

/// <summary>Editing the list of excluded voices.</summary>
public static class VoiceExclusions
{
    /// <summary>The list with an entry added once, whatever the case it was written in.</summary>
    /// <param name="current">The entries already excluded.</param>
    /// <param name="entry">The model, or <c>model:speaker</c>, to add.</param>
    public static string[] With(IEnumerable<string> current, string entry)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);

        return
        [
            .. current.Where(existing => !string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase)),
            entry,
        ];
    }
}
