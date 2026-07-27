using StormATC.Domain.Atc;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Phraseology;

/// <summary>
/// Turns an approved ATC decision into the words a controller says.
/// </summary>
public interface IPhraseologyRenderer
{
    /// <summary>A stable identifier for this renderer, recorded in diagnostics.</summary>
    string RendererName { get; }

    /// <summary>Renders a decision into speakable and displayable text.</summary>
    Task<RenderedPhraseology> RenderAsync(
        AtcDecision decision,
        AtcSession session,
        CancellationToken cancellationToken);
}

/// <summary>A rendered controller transmission.</summary>
public sealed record RenderedPhraseology
{
    /// <summary>The text to speak.</summary>
    public required string SpokenText { get; init; }

    /// <summary>The abbreviated text to show on screen.</summary>
    public required string DisplayText { get; init; }

    /// <summary>
    /// True when this came from the deterministic renderer rather than the language model —
    /// either because no model is configured, the call failed, or validation rejected the
    /// output. Surfaced in the transcript so the user can see which path was taken.
    /// </summary>
    public required bool UsedDeterministicFallback { get; init; }

    /// <summary>Why the fallback was used, when it was.</summary>
    public string? FallbackReason { get; init; }

    /// <summary>Validation problems found in the model output that was rejected.</summary>
    public IReadOnlyList<string> ValidationProblems { get; init; } = [];

    /// <summary>How long rendering took.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// The always-available renderer. Produces correct phraseology from the decision's own
/// deterministic fallback text.
/// </summary>
/// <remarks>
/// Every <see cref="AtcDecision"/> carries a <see cref="AtcDecision.FallbackPhraseology"/> that
/// the state engine generated from validated value objects. This renderer simply formats it,
/// which is why there is no configuration under which StormATC has nothing to say.
/// </remarks>
public sealed class DeterministicPhraseologyRenderer : IPhraseologyRenderer
{
    /// <inheritdoc />
    public string RendererName => "Deterministic";

    /// <inheritdoc />
    public Task<RenderedPhraseology> RenderAsync(
        AtcDecision decision,
        AtcSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(session);

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Render(decision, session));
    }

    /// <summary>Renders synchronously.</summary>
    public static RenderedPhraseology Render(AtcDecision decision, AtcSession session)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(session);

        string spoken = decision.FallbackPhraseology;
        string display = session.Callsign is Callsign callsign
            ? Domain.Phraseology.DeterministicPhraseology.ToDisplayForm(spoken, callsign)
            : spoken;

        return new RenderedPhraseology
        {
            SpokenText = spoken,
            DisplayText = display,
            UsedDeterministicFallback = true,
        };
    }
}
