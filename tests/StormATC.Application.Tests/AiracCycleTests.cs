using StormATC.Application.Charts;

namespace StormATC.Application.Tests;

/// <summary>The AIRAC cycles and the notice of the charts folder.</summary>
public class AiracCycleTests
{
    [Theory]
    [InlineData(2026, 10, 1, 2026, 10, 1)]
    [InlineData(2026, 10, 23, 2026, 10, 1)]
    [InlineData(2026, 10, 28, 2026, 10, 1)]
    [InlineData(2026, 10, 29, 2026, 10, 29)]
    [InlineData(2026, 9, 30, 2026, 9, 3)]
    [InlineData(2027, 1, 1, 2026, 12, 24)]
    public void The_cycle_in_effect_starts_every_28_days(int year, int month, int day, int cycleYear, int cycleMonth, int cycleDay) =>
        Assert.Equal(new DateOnly(cycleYear, cycleMonth, cycleDay), AiracCycle.InEffect(new DateOnly(year, month, day)));

    [Fact]
    public void A_chart_is_addressed_in_the_eAIP_folder_of_its_cycle() =>
        Assert.Equal(
            "https://www.sia.aviation-civile.gouv.fr/media/dvd/eAIP_01_OCT_2026/Atlas-VAC/PDF_AIPparSSection/VAC/AD/AD-2.LFPL.pdf",
            AiracCycle.ChartUri(new DateOnly(2026, 10, 1), "lfpl").ToString());

    [Fact]
    public void The_notice_gives_the_source_the_cycle_and_the_download_date()
    {
        string notice = AiracCycle.Notice(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 23));

        Assert.Contains("SIA", notice, StringComparison.Ordinal);
        Assert.Contains("2026-10-23", notice, StringComparison.Ordinal);
        Assert.Equal(new DateOnly(2026, 10, 1), AiracCycle.CycleOf(notice));
        Assert.Null(AiracCycle.CycleOf("no cycle here"));
    }
}
