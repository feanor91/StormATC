using StormATC.Infrastructure.Simulator;
using Microsoft.Extensions.Options;
using StormATC.Application.Options;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Tests;

public sealed class SimConnectValueConversionTests
{
    [Theory]
    [InlineData(132.150, 132_150_000u)]
    [InlineData(130.655, 130_655_000u)]
    public void Com_frequency_is_sent_to_SimConnect_as_integer_hertz(
        double megahertz,
        uint expected)
    {
        Frequency frequency = Frequency.FromMegahertz((decimal)megahertz);

        Assert.Equal(expected, MsfsSimConnectConnector.ToFrequencyHertz(frequency));
    }

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
            Options.Create(new SimulatorOptions()),
            new StubOptionsMonitor<TrafficOptions>(new TrafficOptions()),
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

    private sealed class StubOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
#endif
