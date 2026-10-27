namespace StormATC.Infrastructure.Simulator;

/// <summary>Reads the simulator's CAMERA STATE.</summary>
internal static class MsfsCameraState
{
    /// <summary>
    /// True when the camera is on a screen of the simulator rather than on the aircraft: the world map, the hangar, the
    /// menu and in-game cinematics, a replay. The aircraft can be loaded with real data behind them, so the position alone
    /// does not tell that the pilot is in it.
    /// </summary>
    /// <param name="cameraState">The value of CAMERA STATE.</param>
    internal static bool IsMenu(int cameraState) => cameraState is >= 11 and <= 17 or 19;
}
