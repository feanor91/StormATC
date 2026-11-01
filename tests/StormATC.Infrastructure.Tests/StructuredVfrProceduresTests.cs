using System.Text.Json;

namespace StormATC.Infrastructure.Tests;

/// <summary>The big aerodromes whose procedures were structured by hand from the chart text.</summary>
public sealed class StructuredVfrProceduresTests
{
    private static JsonElement Profile(string icao)
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "vfr-profiles", icao + ".json"));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return document.RootElement.Clone();
    }

    private static IEnumerable<string> Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Array:
                foreach (string s in element.EnumerateArray().SelectMany(Strings))
                {
                    yield return s;
                }

                break;
            case JsonValueKind.Object:
                foreach (string s in element.EnumerateObject().SelectMany(p => Strings(p.Value)))
                {
                    yield return s;
                }

                break;
        }
    }

    [Theory]
    [InlineData("LFML")]
    [InlineData("LFLL")]
    [InlineData("LFBD")]
    [InlineData("LFST")]
    [InlineData("LFRS")]
    [InlineData("LFMT")]
    [InlineData("LFKJ")]
    public void The_procedures_are_structured_and_the_flattened_table_text_is_gone(string icao)
    {
        JsonElement profile = Profile(icao);

        Assert.True(profile.TryGetProperty("withAts", out JsonElement withAts));
        Assert.True(withAts.TryGetProperty("arrivals", out JsonElement arrivals) && arrivals.GetArrayLength() > 0
            || withAts.TryGetProperty("radio", out _));
        Assert.Contains("structured by hand", string.Join(' ', Strings(profile.GetProperty("$comment"))));

        // The one-line debris of the old script (a table header glued to the next paragraph) must not remain.
        foreach (string note in Strings(profile.GetProperty("notes")))
        {
            Assert.DoesNotContain("Reporting points (ALT in ft)", note);
            Assert.DoesNotContain("Trajectories at arrival", note);
        }
    }

    [Fact]
    public void Marseille_lists_each_entry_point_with_its_altitudes()
    {
        string text = string.Join('\n', Strings(Profile("LFML").GetProperty("withAts")));

        Assert.Contains("LP (1700 ft) or CB (2000 ft), then NA (1500 ft), then NB (1500 ft)", text);
        Assert.Contains("SW (700 ft MAX), then S (1000 ft)", text);
        Assert.Contains("Provence 132.950", text);
    }

    [Fact]
    public void Montpellier_prefers_runway_30_and_gives_the_entry_altitudes()
    {
        JsonElement profile = Profile("LFMT");

        Assert.Equal(303, profile.GetProperty("preferredQfu").GetInt32());
        JsonElement arrivals = profile.GetProperty("withAts").GetProperty("arrivals");
        JsonElement es = arrivals.EnumerateArray().Single(a => a.TryGetProperty("via", out JsonElement via) && via.GetString() == "ES");
        Assert.Equal(1000, es.GetProperty("altitudeMaxFeet").GetInt32());
        Assert.Equal("118.775", es.GetProperty("contact").GetString());
    }
}
