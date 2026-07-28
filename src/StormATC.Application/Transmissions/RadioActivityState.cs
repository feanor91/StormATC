namespace StormATC.Application.Transmissions;

/// <summary>
/// Coordinates user push-to-talk activity with optional background radio simulation.
/// </summary>
/// <remarks>
/// Traffic chatter must always yield to the user. This small process-local signal keeps that
/// rule independent of the desktop view model and is safe to read from a background service.
/// </remarks>
public sealed class RadioActivityState
{
    private int _userIsTransmitting;

    /// <summary>True while the user's push-to-talk capture is active.</summary>
    public bool UserIsTransmitting => Volatile.Read(ref _userIsTransmitting) != 0;

    /// <summary>Updates the push-to-talk state.</summary>
    public void SetUserTransmitting(bool isTransmitting) =>
        Volatile.Write(ref _userIsTransmitting, isTransmitting ? 1 : 0);
}
