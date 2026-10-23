using System.Globalization;
using System.Text.RegularExpressions;

namespace StormATC.Application.Charts;

/// <summary>Where the charts in the folder come from, as the notice in the folder says it.</summary>
/// <param name="Text">The notice: the source, the AIRAC cycle and the date of the download.</param>
/// <param name="CycleEffective">The date the AIRAC cycle came into effect, when the notice gives it.</param>
public sealed record VacChartSource(string Text, DateOnly? CycleEffective);

/// <summary>What an update of the charts did.</summary>
/// <param name="Cycle">The AIRAC cycle the charts were taken from.</param>
/// <param name="Changed">Charts replaced because the SIA published a different one.</param>
/// <param name="Added">Charts downloaded that the folder did not have.</param>
/// <param name="Unchanged">Charts that were already the published ones.</param>
/// <param name="NotPublished">Aerodromes for which the SIA publishes no chart.</param>
/// <param name="Failed">Charts that could not be downloaded or written.</param>
public sealed record VacChartUpdateResult(DateOnly Cycle, int Changed, int Added, int Unchanged, int NotPublished, int Failed);

/// <summary>Updates the charts in the charts folder from the SIA, on the pilot's request.</summary>
public interface IVacChartUpdater
{
    /// <summary>
    /// Downloads the charts of the AIRAC cycle in effect and replaces the ones that changed. The charts are kept as
    /// published. Contacts the SIA's website only.
    /// </summary>
    /// <param name="includeMissing">Also download the charts of the aerodromes the folder does not have.</param>
    /// <param name="progress">Receives a short line as the update goes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VacChartUpdateResult> UpdateAsync(bool includeMissing, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>The AIRAC cycles: every 28 days, from a known effective date.</summary>
public static partial class AiracCycle
{
    private static readonly DateOnly Reference = new(2026, 10, 1);

    /// <summary>The effective date of the cycle in effect on a day.</summary>
    /// <param name="today">The day.</param>
    public static DateOnly InEffect(DateOnly today)
    {
        int cycles = (int)Math.Floor((today.DayNumber - Reference.DayNumber) / 28.0);
        return Reference.AddDays(cycles * 28);
    }

    /// <summary>The address of a chart in the SIA's eAIP of a cycle.</summary>
    /// <param name="effective">The effective date of the cycle.</param>
    /// <param name="icao">The aerodrome.</param>
    public static Uri ChartUri(DateOnly effective, string icao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icao);

        string folder = "eAIP_" + effective.ToString("dd_MMM_yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
        return new Uri(
            $"https://www.sia.aviation-civile.gouv.fr/media/dvd/{folder}/Atlas-VAC/PDF_AIPparSSection/VAC/AD/AD-2.{icao.ToUpperInvariant()}.pdf");
    }

    /// <summary>The notice written in the charts folder after an update.</summary>
    /// <param name="effective">The effective date of the cycle.</param>
    /// <param name="downloaded">The day the charts were downloaded.</param>
    public static string Notice(DateOnly effective, DateOnly downloaded) =>
        "Source: SIA (Service de l'Information Aeronautique, DGAC), AIP France, atlas des cartes VAC, cycle AIRAC en vigueur le "
        + effective.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        + ". Cartes conservees telles que publiees. Telechargees le "
        + downloaded.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".";

    /// <summary>Reads the cycle date out of a notice, or null.</summary>
    /// <param name="notice">The text of the notice.</param>
    public static DateOnly? CycleOf(string notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        Match match = CycleDate().Match(notice);
        return match.Success
            && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
                ? date
                : null;
    }

    [GeneratedRegex(@"cycle AIRAC en vigueur le (\d{4}-\d{2}-\d{2})")]
    private static partial Regex CycleDate();
}
