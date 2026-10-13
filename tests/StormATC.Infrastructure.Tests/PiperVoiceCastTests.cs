using StormATC.Domain.Phraseology;
using StormATC.Infrastructure.Speech;

namespace StormATC.Infrastructure.Tests;

/// <summary>Casting Piper voices to controllers.</summary>
public sealed class PiperVoiceCastTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "stormatc-voices-" + Guid.NewGuid().ToString("N"));

    public PiperVoiceCastTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Voice(string name, bool withConfig = true)
    {
        string path = Path.Combine(_folder, name + ".onnx");
        File.WriteAllText(path, "x");
        if (withConfig)
        {
            File.WriteAllText(path + ".json", "{}");
        }

        return path;
    }

    [Fact]
    public void Discovery_keeps_the_language_and_requires_the_config_file()
    {
        string tom = Voice("fr_FR-tom-medium");
        string siwis = Voice("fr_FR-siwis-medium");
        Voice("fr_FR-noconfig-medium", withConfig: false);
        string alan = Voice("en_GB-alan-medium");

        IReadOnlyList<string> french = PiperVoiceCast.Discover(_folder, PhraseologyLanguage.French, tom);

        Assert.Equal([tom, siwis], french.Order().ToArray().OrderBy(path => path == tom ? 0 : 1).ToArray());
        Assert.Equal([alan], PiperVoiceCast.Discover(_folder, PhraseologyLanguage.English, null));
    }

    [Fact]
    public void Discovery_without_a_folder_returns_only_the_main_voice()
    {
        string tom = Voice("fr_FR-tom-medium");

        Assert.Equal([tom], PiperVoiceCast.Discover(null, PhraseologyLanguage.French, tom));
        Assert.Empty(PiperVoiceCast.Discover("", PhraseologyLanguage.French, null));
        Assert.Empty(PiperVoiceCast.Discover(Path.Combine(_folder, "missing"), PhraseologyLanguage.French, null));
    }

    [Fact]
    public void A_controller_keeps_its_voice_and_rate()
    {
        var cast = new PiperVoiceCast(new Random(7));
        string[] pool = ["a.onnx", "b.onnx", "c.onnx"];

        ChosenVoice first = cast.Assign("Tower@118.700", pool, 0.9, 0.12);

        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(first, cast.Assign("Tower@118.700", pool, 0.9, 0.12));
        }
    }

    [Fact]
    public void Different_controllers_draw_from_the_whole_cast_within_the_rate_range()
    {
        var cast = new PiperVoiceCast(new Random(3));
        string[] pool = ["a.onnx", "b.onnx", "c.onnx"];

        var voices = Enumerable.Range(0, 60)
            .Select(index => cast.Assign($"Ground@{index}", pool, 0.9, 0.12))
            .ToArray();

        Assert.Equal(3, voices.Select(voice => voice.VoicePath).Distinct().Count());
        Assert.All(voices, voice => Assert.InRange(voice.LengthScale, 0.78, 1.02));
        Assert.True(voices.Select(voice => voice.LengthScale).Distinct().Count() > 3);
    }

    [Fact]
    public void A_voice_removed_from_the_pool_is_replaced()
    {
        var cast = new PiperVoiceCast(new Random(1));

        ChosenVoice first = cast.Assign("Delivery@121.800", ["a.onnx", "b.onnx"], 0.9, 0);
        ChosenVoice after = cast.Assign("Delivery@121.800", ["c.onnx"], 0.9, 0);

        Assert.Equal("c.onnx", after.VoicePath);
        Assert.NotEqual(first.VoicePath, after.VoicePath);
    }

    [Fact]
    public void A_multi_speaker_model_gets_a_speaker_and_keeps_it()
    {
        var cast = new PiperVoiceCast(new Random(11), path => path.StartsWith("multi", StringComparison.Ordinal) ? 125 : 1);

        ChosenVoice voice = cast.Assign("Tower@118.700", ["multi.onnx"], 0.9, 0);

        Assert.NotNull(voice.SpeakerId);
        Assert.InRange(voice.SpeakerId!.Value, 0, 124);
        Assert.Equal(voice, cast.Assign("Tower@118.700", ["multi.onnx"], 0.9, 0));
    }

    [Fact]
    public void A_single_voice_model_has_no_speaker()
    {
        var cast = new PiperVoiceCast(new Random(2), _ => 1);

        Assert.Null(cast.Assign("Ground@121.900", ["single.onnx"], 0.9, 0).SpeakerId);
    }

    [Fact]
    public void Multi_speaker_models_do_not_outweigh_single_voice_models()
    {
        var cast = new PiperVoiceCast(new Random(9), path => path == "many.onnx" ? 900 : 1);
        string[] pool = ["one.onnx", "two.onnx", "many.onnx"];

        int many = Enumerable.Range(0, 300)
            .Count(index => cast.Assign($"K{index}", pool, 0.9, 0).VoicePath == "many.onnx");

        Assert.InRange(many, 70, 130);
    }

    [Fact]
    public void The_speaker_count_comes_from_the_voice_config()
    {
        string multi = Voice("fr_FR-mls-medium");
        File.WriteAllText(multi + ".json", """{"num_speakers": 125}""");
        string single = Voice("fr_FR-tom-medium");

        Assert.Equal(125, PiperVoiceCast.ReadSpeakerCount(multi));
        Assert.Equal(1, PiperVoiceCast.ReadSpeakerCount(single));
        Assert.Equal(1, PiperVoiceCast.ReadSpeakerCount(Path.Combine(_folder, "missing.onnx")));
    }

    [Fact]
    public void No_spread_keeps_the_configured_rate()
    {
        var cast = new PiperVoiceCast(new Random(5));

        Assert.Equal(0.9, cast.Assign("Tower@118.700", ["a.onnx"], 0.9, 0).LengthScale);
    }
}
