using StormATC.Infrastructure.Simulator;

namespace StormATC.Infrastructure.Tests;

public sealed class SimConnectValueConversionTests
{
    [Theory]
    [InlineData(7000, "7000")]
    [InlineData(1234, "1234")]
    [InlineData(456, "0456")]
    [InlineData(0, "0000")]
    public void SafeSquawk_FormatsDecimalNumberUnitWithLeadingZeroes(
        int simulatorValue,
        string expected)
    {
        Assert.Equal(expected, MsfsSimConnectConnector.SafeSquawk(simulatorValue).Digits);
    }

    [Theory]
    [InlineData(1280)]
    [InlineData(8888)]
    [InlineData(-1)]
    [InlineData(10_000)]
    public void SafeSquawk_InvalidDecimalOrNonOctalValueFallsBackToConspicuity(
        int simulatorValue)
    {
        Assert.Equal("7000", MsfsSimConnectConnector.SafeSquawk(simulatorValue).Digits);
    }
}

#if !MSFS_SDK_AVAILABLE
public sealed class SimConnectSdkFallbackTests
{
    [Fact]
    public async Task NoSdkBuildReportsAnExactNonRetryableFailure()
    {
        var connector = new MsfsSimConnectConnector(
            Microsoft.Extensions.Options.Options.Create(
                new StormATC.Application.Options.SimulatorOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MsfsSimConnectConnector>.Instance);

        await connector.ConnectAsync(CancellationToken.None);

        Assert.False(MsfsSimConnectConnector.IsSdkAvailable);
        Assert.Equal(
            StormATC.Domain.SimulatorConnectionState.Unavailable,
            connector.ConnectionState);
        Assert.Equal(
            SimulatorConnectionFailureKind.SdkUnavailable,
            connector.LastFailureKind);
        Assert.Equal(
            MsfsSimConnectConnector.SdkUnavailableMessage,
            connector.StatusMessage);
    }
}
#endif
