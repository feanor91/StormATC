using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using StormATC.Application.Charts;

namespace StormATC.Infrastructure.Charts;

/// <summary>
/// Updates the charts in the charts folder from the SIA's eAIP: one chart at a time, replacing a file only when
/// the published chart is different. It only contacts <c>www.sia.aviation-civile.gouv.fr</c>, only when the pilot
/// asks, and sends nothing about the pilot or the flight.
/// </summary>
public sealed partial class SiaVacChartUpdater(
    HttpClient httpClient,
    IVacChartLibrary library,
    TimeProvider timeProvider,
    ILogger<SiaVacChartUpdater> logger) : IVacChartUpdater
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    /// <inheritdoc />
    public async Task<VacChartUpdateResult> UpdateAsync(
        bool includeMissing,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        DateOnly cycle = AiracCycle.InEffect(today);

        // The cycle is probed with a chart that exists everywhere: if it is not online, nothing is touched.
        if (!await ExistsAsync(AiracCycle.ChartUri(cycle, "LFPL"), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The AIRAC cycle {cycle:yyyy-MM-dd} is not online at the SIA.");
        }

        library.EnsureDirectory();
        SortedSet<string> wanted = new(StringComparer.OrdinalIgnoreCase);
        foreach (VacChart chart in library.List())
        {
            if (chart.Icao is string icao && chart.Id.Equals("AD-2." + icao, StringComparison.OrdinalIgnoreCase))
            {
                wanted.Add(icao);
            }
        }

        // A folder with no chart to update is a folder to fill: there is nothing else an update could do.
        if (includeMissing || wanted.Count == 0)
        {
            foreach (string icao in AerodromeCodes())
            {
                wanted.Add(icao);
            }
        }

        int changed = 0, added = 0, unchanged = 0, notPublished = 0, failed = 0, done = 0;
        string temporary = Path.Combine(Path.GetTempPath(), "stormatc-vac-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            foreach (string icao in wanted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"{++done}/{wanted.Count}  {icao}");
                string target = Path.Combine(library.Directory, $"AD-2.{icao}.pdf");

                try
                {
                    if (!await DownloadAsync(AiracCycle.ChartUri(cycle, icao), temporary, cancellationToken).ConfigureAwait(false))
                    {
                        notPublished++;
                        continue;
                    }

                    if (!File.Exists(target))
                    {
                        File.Copy(temporary, target);
                        added++;
                    }
                    else if (!await SameAsync(temporary, target, cancellationToken).ConfigureAwait(false))
                    {
                        File.Copy(temporary, target, overwrite: true);
                        changed++;
                    }
                    else
                    {
                        unchanged++;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    LogChartFailed(logger, icao, ex);
                    failed++;
                }

                await Task.Delay(Pause, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            File.Delete(temporary);
        }

        if (failed == 0 || changed + added + unchanged > 0)
        {
            await File.WriteAllTextAsync(
                Path.Combine(library.Directory, "_cycle.txt"),
                AiracCycle.Notice(cycle, today) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }

        return new VacChartUpdateResult(cycle, changed, added, unchanged, notPublished, failed);
    }

    private async Task<bool> ExistsAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Downloads a chart to a file. False when the SIA publishes none (404) or the file is not a PDF.</summary>
    private async Task<bool> DownloadAsync(Uri uri, string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        await using (Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (FileStream file = File.Create(path))
        {
            await body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        byte[] header = new byte[4];
        await using FileStream check = File.OpenRead(path);
        int read = await check.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        return read == 4 && header[0] == (byte)'%' && header[1] == (byte)'P' && header[2] == (byte)'D' && header[3] == (byte)'F';
    }

    private static async Task<bool> SameAsync(string first, string second, CancellationToken cancellationToken)
    {
        await using FileStream a = File.OpenRead(first);
        await using FileStream b = File.OpenRead(second);
        byte[] hashA = await SHA256.HashDataAsync(a, cancellationToken).ConfigureAwait(false);
        byte[] hashB = await SHA256.HashDataAsync(b, cancellationToken).ConfigureAwait(false);
        return hashA.AsSpan().SequenceEqual(hashB);
    }

    /// <summary>The ICAO codes of the list of French aerodromes shipped with the application.</summary>
    private static IEnumerable<string> AerodromeCodes()
    {
        string list = Path.Combine(AppContext.BaseDirectory, "data", "aerodromes-france.csv");
        if (!File.Exists(list))
        {
            yield break;
        }

        foreach (string line in File.ReadLines(list).Skip(1))
        {
            string[] columns = line.Split(';');
            string code = columns[0].Trim().TrimStart('﻿');

            // The last column says whether the SIA publishes a VAC chart for the aerodrome.
            if (columns.Length > 4 && columns[4].Trim().Equals("non", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (code.Length == 4 && code.StartsWith("LF", StringComparison.Ordinal)
                && code.All(char.IsAsciiLetterUpper))
            {
                yield return code;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The chart of {Icao} could not be updated.")]
    private static partial void LogChartFailed(ILogger logger, string icao, Exception exception);
}
