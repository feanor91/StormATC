using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace StormATC.Application.FlightPlanning;

/// <summary>
/// A message published by the StormATC WASM module in the simulator: a flight plan route and where
/// it came from.
/// </summary>
/// <param name="Sequence">Counts the module's messages, so a message already seen can be skipped.</param>
/// <param name="Source">One of <see cref="StormATC.Application.Abstractions.SimRouteSources"/>.</param>
/// <param name="RouteJson">The route, in the shape the <see cref="PlannedRouteParser"/> reads.</param>
public sealed record SimRouteMessage(long Sequence, string Source, string RouteJson)
{
    /// <summary>Reads the module's message: <c>{"seq":…,"source":"atc","route":{…}}</c>.</summary>
    /// <param name="json">The text read from the client data area.</param>
    /// <param name="message">The message, when the text holds one.</param>
    public static bool TryParse(string? json, [NotNullWhen(true)] out SimRouteMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("route", out JsonElement route)
                || route.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("source", out JsonElement source)
                || source.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            long sequence = root.TryGetProperty("seq", out JsonElement seq) && seq.TryGetInt64(out long value)
                ? value
                : 0;

            message = new SimRouteMessage(sequence, source.GetString() ?? string.Empty, route.GetRawText());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
