using System.Text.RegularExpressions;

namespace StormATC.Application.Charts;

/// <summary>A chart (a PDF) in the application's charts folder.</summary>
/// <param name="Id">What the panel asks for it by: the file's name without its extension.</param>
/// <param name="Name">The name shown to the pilot.</param>
/// <param name="Icao">The aerodrome the file's name mentions, such as <c>LFPL</c>, or null.</param>
public sealed record VacChart(string Id, string Name, string? Icao)
{
    /// <summary>The name of the aerodrome the chart is of, when it is known (<c>Lognes-Émerainville</c>).</summary>
    public string? AerodromeName => AerodromeNames.Find(Icao);
}

/// <summary>One page of a chart, drawn as a PNG image.</summary>
/// <param name="Png">The image.</param>
/// <param name="PageNumber">The page, from 1.</param>
/// <param name="PageCount">How many pages the chart has.</param>
public sealed record VacChartPage(byte[] Png, int PageNumber, int PageCount);

/// <summary>The charts the pilot put in StormATC's charts folder.</summary>
public interface IVacChartLibrary
{
    /// <summary>The folder the charts are read from.</summary>
    string Directory { get; }

    /// <summary>Creates the folder when it does not exist yet, so the pilot can see where to put the charts.</summary>
    void EnsureDirectory();

    /// <summary>The charts in the folder, by name. A file saved twice (<c>chart (1).pdf</c>) is listed once.</summary>
    IReadOnlyList<VacChart> List();

    /// <summary>How many pages a chart has, or null when it is unknown or cannot be read.</summary>
    /// <param name="id">The chart.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int?> GetPageCountAsync(string id, CancellationToken cancellationToken);

    /// <summary>Draws a page, or returns null when the chart or the page does not exist or cannot be read.</summary>
    /// <param name="id">The chart.</param>
    /// <param name="page">The page, from 1.</param>
    /// <param name="width">The width of the image in pixels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VacChartPage?> RenderPageAsync(string id, int page, int width, CancellationToken cancellationToken);
}

/// <summary>Draws the pages of a PDF. Implemented where the operating system can do it.</summary>
public interface IVacPageRenderer
{
    /// <summary>How many pages the PDF has.</summary>
    /// <param name="pdfPath">The file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken);

    /// <summary>Draws a page as a PNG image.</summary>
    /// <param name="pdfPath">The file.</param>
    /// <param name="page">The page, from 1.</param>
    /// <param name="width">The width of the image in pixels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<byte[]> RenderPageAsync(string pdfPath, int page, int width, CancellationToken cancellationToken);
}

/// <summary>Names a chart from its file name.</summary>
public static partial class VacChartNames
{
    /// <summary>
    /// Takes a file name such as <c>AD-2.LFPL (1).pdf</c> and gives the name shown (<c>AD-2.LFPL</c>)
    /// and the aerodrome it mentions (<c>LFPL</c>).
    /// </summary>
    /// <param name="fileName">The file's name.</param>
    public static (string Name, string? Icao) Describe(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        string stem = Path.GetFileNameWithoutExtension(fileName).Trim();

        // A browser saves a second copy of a download as "name (1)".
        stem = CopySuffix().Replace(stem, string.Empty).Trim();

        string upper = stem.ToUpperInvariant();
        Match french = FrenchIcao().Match(upper);
        Match any = french.Success ? french : AnyIcao().Match(upper);
        return (stem, any.Success ? any.Value : null);
    }

    [GeneratedRegex(@"\s*\(\d+\)$")]
    private static partial Regex CopySuffix();

    [GeneratedRegex(@"(?<![A-Z0-9])LF[A-Z]{2}(?![A-Z0-9])")]
    private static partial Regex FrenchIcao();

    [GeneratedRegex(@"(?<![A-Z0-9])[A-Z]{4}(?![A-Z0-9])")]
    private static partial Regex AnyIcao();
}
