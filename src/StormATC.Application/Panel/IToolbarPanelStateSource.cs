using StormATC.Application.Abstractions;
using StormATC.Application.Sessions;

namespace StormATC.Application.Panel;

/// <summary>Supplies the state the in-simulator panel shows.</summary>
public interface IToolbarPanelStateSource
{
    /// <summary>The state right now.</summary>
    ToolbarPanelState Current();
}

/// <summary>The panel state of the session that is running.</summary>
/// <param name="sessionContext">The running session.</param>
/// <param name="voices">The voice that spoke last, when a local voice engine reports it.</param>
public sealed class SessionToolbarPanelStateSource(
    AtcSessionContext sessionContext,
    IVoiceRejection? voices = null) : IToolbarPanelStateSource
{
    /// <inheritdoc />
    public ToolbarPanelState Current() =>
        ToolbarPanelStateBuilder.Build(sessionContext.CurrentSession, voices?.LastVoice, sessionContext.CachedCurrentVfrProfile());
}
