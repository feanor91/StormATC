using StormATC.Infrastructure.Simulator;

namespace StormATC.Infrastructure.Tests;

/// <summary>Which values of the simulator's camera state mean the pilot is not in the aircraft.</summary>
public class MenuCameraStateTests
{
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    [InlineData(12, true)]
    [InlineData(13, true)]
    [InlineData(15, true)]
    [InlineData(16, true)]
    [InlineData(17, true)]
    [InlineData(19, true)]
    [InlineData(20, false)]
    public void The_camera_state_tells_a_menu_from_the_aircraft(int cameraState, bool expected) =>
        Assert.Equal(expected, MsfsCameraState.IsMenu(cameraState));
}
