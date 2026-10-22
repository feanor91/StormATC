using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Charts;
using StormATC.Application.Options;

namespace StormATC.Infrastructure.Charts;

/// <summary>
/// The charts in the <c>Cartes VAC</c> folder beside the application: the PDFs the pilot put there,
/// drawn page by page for the in-simulator panel.
/// </summary>
/// <remarks>
/// <para>
/// The charts are the pilot's own files (the AIP charts are published by the national authority and
/// are not distributed with StormATC). Only PDF files directly in the folder are listed, and a chart
/// is asked for by its listed name, never by a path.
/// </para>
/// <para>
/// A page is drawn once and kept for a while: the panel asks again whenever the pilot changes page
/// or zoom.
/// </para>
/// </remarks>
public sealed class VacChartLibrary : IVacChartLibrary, IDisposable
{
    /// <summary>The name of the folder beside the application.</summary>
    public const string FolderName = "Cartes VAC";

    private const int MinimumWidth = 400;
    private const int MaximumWidth = 4096;
    private const int PagesKept = 24;

    private readonly string _directory;
    private readonly IVacPageRenderer? _renderer;
    private readonly ILogger<VacChartLibrary> _logger;
    private readonly SemaphoreSlim _drawing = new(1, 1);
    private readonly ConcurrentDictionary<string, byte[]> _pages = new();
    private readonly ConcurrentQueue<string> _order = new();
    private readonly ConcurrentDictionary<string, int> _pageCounts = new();

    /// <summary>Creates the library.</summary>
    /// <param name="options">Where the charts folder is, when it is not the default.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="renderer">Draws the PDF pages; null where the system cannot, and no page is then drawn.</param>
    public VacChartLibrary(
        IOptions<ToolbarPanelOptions> options,
        ILogger<VacChartLibrary> logger,
        IVacPageRenderer? renderer = null)
        : this(Resolve(options?.Value.ChartsDirectory, AppContext.BaseDirectory), logger, renderer)
    {
    }

    /// <summary>Creates the library on a chosen folder.</summary>
    /// <param name="directory">The folder.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="renderer">Draws the PDF pages.</param>
    internal VacChartLibrary(string directory, ILogger<VacChartLibrary> logger, IVacPageRenderer? renderer)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _renderer = renderer;
    }

    /// <inheritdoc />
    public string Directory => _directory;

    /// <inheritdoc />
    public void Dispose() => _drawing.Dispose();

    /// <summary>
    /// The folder: the one the settings name, else <c>Cartes VAC</c> beside the application. A
    /// developer's checkout has the charts at the root of the repository, next to the solution: the
    /// build folder has none, so that one is used when it holds charts and the application's does not.
    /// </summary>
    /// <param name="configured">The folder the settings name, or empty.</param>
    /// <param name="applicationDirectory">The folder the application runs from.</param>
    internal static string Resolve(string? configured, string applicationDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
        }

        string beside = Path.Combine(applicationDirectory, FolderName);
        if (HasCharts(beside))
        {
            return beside;
        }

        for (DirectoryInfo? parent = new DirectoryInfo(applicationDirectory).Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.EnumerateFiles("StormATC.sln").Any())
            {
                string checkout = Path.Combine(parent.FullName, FolderName);
                return HasCharts(checkout) ? checkout : beside;
            }
        }

        return beside;
    }

    private static bool HasCharts(string directory)
    {
        try
        {
            return System.IO.Directory.Exists(directory)
                && System.IO.Directory.EnumerateFiles(directory, "*.pdf").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void EnsureDirectory()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The charts folder {Directory} could not be created.", _directory);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<VacChart> List() => [.. Files().Select(file => file.Chart)];

    /// <inheritdoc />
    public async Task<int?> GetPageCountAsync(string id, CancellationToken cancellationToken)
    {
        if (Find(id) is not { } file || _renderer is null)
        {
            return null;
        }

        string key = file.Path + "|" + file.Stamp;
        if (_pageCounts.TryGetValue(key, out int known))
        {
            return known;
        }

        await _drawing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int count = await _renderer.GetPageCountAsync(file.Path, cancellationToken).ConfigureAwait(false);
            _pageCounts[key] = count;
            return count;
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            _logger.LogWarning(ex, "The chart {Chart} could not be opened.", file.Chart.Name);
            return null;
        }
        finally
        {
            _drawing.Release();
        }
    }

    /// <inheritdoc />
    public async Task<VacChartPage?> RenderPageAsync(string id, int page, int width, CancellationToken cancellationToken)
    {
        if (Find(id) is not { } file || _renderer is null || page < 1)
        {
            return null;
        }

        int pages = await GetPageCountAsync(id, cancellationToken).ConfigureAwait(false) ?? 0;
        if (page > pages)
        {
            return null;
        }

        int size = Math.Clamp(width, MinimumWidth, MaximumWidth);
        string key = $"{file.Path}|{file.Stamp}|{page}|{size}";
        if (_pages.TryGetValue(key, out byte[]? kept))
        {
            return new VacChartPage(kept, page, pages);
        }

        await _drawing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] png = await _renderer.RenderPageAsync(file.Path, page, size, cancellationToken).ConfigureAwait(false);
            Keep(key, png);
            return new VacChartPage(png, page, pages);
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            _logger.LogWarning(ex, "Page {Page} of the chart {Chart} could not be drawn.", page, file.Chart.Name);
            return null;
        }
        finally
        {
            _drawing.Release();
        }
    }

    private void Keep(string key, byte[] png)
    {
        _pages[key] = png;
        _order.Enqueue(key);
        while (_order.Count > PagesKept && _order.TryDequeue(out string? oldest))
        {
            _pages.TryRemove(oldest, out _);
        }
    }

    private static bool IsUnreadable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException or NotSupportedException;

    private ChartFile? Find(string id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Files().FirstOrDefault(file => file.Chart.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private sealed record ChartFile(VacChart Chart, string Path, string Stamp);

    /// <summary>The charts, by name, each listed once.</summary>
    private List<ChartFile> Files()
    {
        List<ChartFile> files = [];
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return files;
            }

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in System.IO.Directory
                .EnumerateFiles(_directory, "*.pdf", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                (string name, string? icao) = VacChartNames.Describe(System.IO.Path.GetFileName(path));
                if (name.Length == 0 || !seen.Add(name))
                {
                    continue;
                }

                var info = new FileInfo(path);
                files.Add(new ChartFile(
                    new VacChart(name, name, icao),
                    path,
                    $"{info.Length}-{info.LastWriteTimeUtc.Ticks}"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The charts folder {Directory} could not be read.", _directory);
        }

        return files;
    }
}
