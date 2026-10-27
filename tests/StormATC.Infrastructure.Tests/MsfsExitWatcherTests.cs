using StormATC.Infrastructure.Simulator;

namespace StormATC.Infrastructure.Tests;

/// <summary>When StormATC closes because MSFS stopped.</summary>
public class MsfsExitWatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void It_closes_once_msfs_has_been_gone_for_the_grace_period()
    {
        var watcher = new MsfsExitWatcher();

        Assert.False(watcher.ShouldClose(true, T0, true));
        Assert.False(watcher.ShouldClose(false, T0.AddSeconds(5), true));
        Assert.False(watcher.ShouldClose(false, T0.AddSeconds(15), true));
        Assert.True(watcher.ShouldClose(false, T0.AddSeconds(26), true));
    }

    [Fact]
    public void A_restart_of_msfs_within_the_grace_period_does_not_close_it()
    {
        var watcher = new MsfsExitWatcher();
        watcher.ShouldClose(true, T0, true);
        watcher.ShouldClose(false, T0.AddSeconds(5), true);
        watcher.ShouldClose(true, T0.AddSeconds(10), true);

        Assert.False(watcher.ShouldClose(false, T0.AddSeconds(15), true));
        Assert.False(watcher.ShouldClose(false, T0.AddSeconds(30), true));
    }

    [Fact]
    public void It_never_closes_before_msfs_was_seen_or_when_the_option_is_off()
    {
        var alone = new MsfsExitWatcher();
        Assert.False(alone.ShouldClose(false, T0, true));
        Assert.False(alone.ShouldClose(false, T0.AddMinutes(5), true));

        var off = new MsfsExitWatcher();
        off.ShouldClose(true, T0, false);
        Assert.False(off.ShouldClose(false, T0.AddSeconds(5), false));
        Assert.False(off.ShouldClose(false, T0.AddMinutes(5), false));
    }
}
