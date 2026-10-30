using System.Globalization;
using System.Text.RegularExpressions;

namespace StormATC.Domain.Weather;

/// <summary>Reads a METAR or SPECI into a <see cref="DecodedMetar"/>.</summary>
public static partial class MetarParser
{
    private const double KnotsPerMetrePerSecond = 1.943844;
    private const double MetresPerStatuteMile = 1609.344;
    private const double HectopascalsPerInchOfMercury = 33.8639;

    /// <summary>Reads a report, returning false when it holds no aerodrome and no time.</summary>
    /// <param name="raw">The report as received.</param>
    /// <param name="metar">The decoded report.</param>
    public static bool TryParse(string? raw, out DecodedMetar metar)
    {
        metar = null!;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string text = raw.Trim().TrimEnd('=').Trim();
        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int index = 0;

        if (index < tokens.Length && tokens[index] is "METAR" or "SPECI")
        {
            index++;
        }

        bool correction = false;
        if (index < tokens.Length && tokens[index] is "COR")
        {
            correction = true;
            index++;
        }

        if (index >= tokens.Length || !StationPattern().IsMatch(tokens[index]))
        {
            return false;
        }

        string station = tokens[index++];
        if (index >= tokens.Length || TimePattern().Match(tokens[index]) is not { Success: true } time)
        {
            return false;
        }

        index++;
        var state = new ParseState();
        for (; index < tokens.Length; index++)
        {
            string token = tokens[index];
            if (token == "RMK")
            {
                state.Remarks = string.Join(' ', tokens[(index + 1)..]);
                break;
            }

            if (state.Trend is not null || token is "NOSIG" or "BECMG" or "TEMPO")
            {
                state.Trend = state.Trend is null ? token : $"{state.Trend} {token}";
                continue;
            }

            ReadGroup(token, tokens, index, state);
        }

        metar = new DecodedMetar
        {
            Raw = text,
            Station = station,
            Day = int.Parse(time.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
            Hour = int.Parse(time.Groups[2].ValueSpan, CultureInfo.InvariantCulture),
            Minute = int.Parse(time.Groups[3].ValueSpan, CultureInfo.InvariantCulture),
            IsAutomatic = state.Automatic,
            IsCorrection = correction,
            WindDirectionDegrees = state.WindDirection,
            WindIsVariable = state.WindVariable,
            WindSpeedKnots = state.WindSpeed,
            GustKnots = state.Gust,
            WindVariationFromDegrees = state.VariationFrom,
            WindVariationToDegrees = state.VariationTo,
            VisibilityMeters = state.Visibility,
            IsCavok = state.Cavok,
            Phenomena = state.Phenomena,
            Clouds = state.Clouds,
            NoSignificantCloud = state.NoSignificantCloud,
            TemperatureCelsius = state.Temperature,
            DewPointCelsius = state.DewPoint,
            QnhHectopascals = state.Qnh,
            Trend = state.Trend,
            Remarks = state.Remarks,
        };
        return true;
    }

    private static void ReadGroup(string token, string[] tokens, int index, ParseState state)
    {
        if (token == "AUTO")
        {
            state.Automatic = true;
        }
        else if (token == "CAVOK")
        {
            state.Cavok = true;
        }
        else if (WindPattern().Match(token) is { Success: true } wind)
        {
            ReadWind(wind, state);
        }
        else if (WindVariationPattern().Match(token) is { Success: true } variation)
        {
            state.VariationFrom = int.Parse(variation.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
            state.VariationTo = int.Parse(variation.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
        }
        else if (MetresVisibilityPattern().IsMatch(token))
        {
            state.Visibility ??= int.Parse(token, CultureInfo.InvariantCulture);
        }
        else if (StatuteMilesPattern().Match(token) is { Success: true } miles)
        {
            // "1 1/2SM" is two tokens: the whole miles just read, then the fraction.
            double whole = index > 0 && WholeMilesPattern().IsMatch(tokens[index - 1])
                ? int.Parse(tokens[index - 1], CultureInfo.InvariantCulture)
                : 0;
            double value = whole + SymbolToMiles(miles);
            state.Visibility ??= (int)Math.Round(value * MetresPerStatuteMile);
        }
        else if (PhenomenonPattern().IsMatch(token))
        {
            state.Phenomena.Add(token);
        }
        else if (token is "NSC" or "NCD" or "SKC" or "CLR")
        {
            state.NoSignificantCloud = true;
        }
        else if (CloudPattern().Match(token) is { Success: true } cloud)
        {
            string height = cloud.Groups[2].Value;
            state.Clouds.Add(new MetarCloudLayer(
                cloud.Groups[1].Value,
                int.TryParse(height, NumberStyles.None, CultureInfo.InvariantCulture, out int hundreds)
                    ? hundreds * 100
                    : null,
                cloud.Groups[3].Success ? cloud.Groups[3].Value : null));
        }
        else if (TemperaturePattern().Match(token) is { Success: true } temperature)
        {
            state.Temperature = SignedTemperature(temperature.Groups[1].Value);
            state.DewPoint = temperature.Groups[2].Success ? SignedTemperature(temperature.Groups[2].Value) : null;
        }
        else if (QnhPattern().Match(token) is { Success: true } qnh)
        {
            state.Qnh = int.Parse(qnh.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        }
        else if (AltimeterPattern().Match(token) is { Success: true } altimeter)
        {
            double inches = int.Parse(altimeter.Groups[1].ValueSpan, CultureInfo.InvariantCulture) / 100d;
            state.Qnh = (int)Math.Round(inches * HectopascalsPerInchOfMercury);
        }
    }

    private static void ReadWind(Match wind, ParseState state)
    {
        string direction = wind.Groups[1].Value;
        if (direction == "VRB")
        {
            state.WindVariable = true;
        }
        else if (direction != "///")
        {
            state.WindDirection = int.Parse(direction, CultureInfo.InvariantCulture);
        }

        bool metresPerSecond = wind.Groups[5].Value == "MPS";
        state.WindSpeed = SpeedInKnots(wind.Groups[2].Value, metresPerSecond);
        if (wind.Groups[4].Success)
        {
            state.Gust = SpeedInKnots(wind.Groups[4].Value, metresPerSecond);
        }
    }

    private static int? SpeedInKnots(string digits, bool metresPerSecond)
    {
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int speed))
        {
            return null;
        }

        return metresPerSecond ? (int)Math.Round(speed * KnotsPerMetrePerSecond) : speed;
    }

    private static double SymbolToMiles(Match miles) =>
        miles.Groups[2].Success
            ? int.Parse(miles.Groups[1].ValueSpan, CultureInfo.InvariantCulture)
                / (double)int.Parse(miles.Groups[2].ValueSpan, CultureInfo.InvariantCulture)
            : int.Parse(miles.Groups[1].ValueSpan, CultureInfo.InvariantCulture);

    private static int SignedTemperature(string text) =>
        text[0] == 'M'
            ? -int.Parse(text.AsSpan(1), CultureInfo.InvariantCulture)
            : int.Parse(text, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[A-Z][A-Z0-9]{3}$")]
    private static partial Regex StationPattern();

    [GeneratedRegex(@"^(\d{2})(\d{2})(\d{2})Z$")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^(\d{3}|VRB|///)(\d{2,3})(G(\d{2,3}))?(KT|MPS)$")]
    private static partial Regex WindPattern();

    [GeneratedRegex(@"^(\d{3})V(\d{3})$")]
    private static partial Regex WindVariationPattern();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex MetresVisibilityPattern();

    [GeneratedRegex(@"^(\d+)(?:/(\d+))?SM$")]
    private static partial Regex StatuteMilesPattern();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex WholeMilesPattern();

    [GeneratedRegex(@"^[-+]?(VC)?(MI|PR|BC|DR|BL|SH|TS|FZ)?(DZ|RA|SN|SG|IC|PL|GR|GS|UP|BR|FG|FU|VA|DU|SA|HZ|PY|PO|SQ|FC|SS|DS)+$")]
    private static partial Regex PhenomenonPattern();

    [GeneratedRegex(@"^(FEW|SCT|BKN|OVC|VV)(\d{3}|///)(CB|TCU)?$")]
    private static partial Regex CloudPattern();

    [GeneratedRegex(@"^(M?\d{2})/(M?\d{2})?$")]
    private static partial Regex TemperaturePattern();

    [GeneratedRegex(@"^Q(\d{4})$")]
    private static partial Regex QnhPattern();

    [GeneratedRegex(@"^A(\d{4})$")]
    private static partial Regex AltimeterPattern();

    private sealed class ParseState
    {
        public bool Automatic { get; set; }

        public bool Cavok { get; set; }

        public bool WindVariable { get; set; }

        public int? WindDirection { get; set; }

        public int? WindSpeed { get; set; }

        public int? Gust { get; set; }

        public int? VariationFrom { get; set; }

        public int? VariationTo { get; set; }

        public int? Visibility { get; set; }

        public List<string> Phenomena { get; } = [];

        public List<MetarCloudLayer> Clouds { get; } = [];

        public bool NoSignificantCloud { get; set; }

        public int? Temperature { get; set; }

        public int? DewPoint { get; set; }

        public int? Qnh { get; set; }

        public string? Trend { get; set; }

        public string? Remarks { get; set; }
    }
}
