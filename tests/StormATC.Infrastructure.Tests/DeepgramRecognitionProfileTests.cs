using StormATC.Application.Abstractions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.Speech;

namespace StormATC.Infrastructure.Tests;

public class DeepgramRecognitionProfileTests
{
    [Fact]
    public void Full_core_audio_microphone_names_replace_truncated_winmm_labels()
    {
        IReadOnlyList<string> names =
            DeepgramSpeechRecognitionService.PreferFullInputDeviceNames(
                ["Microphone (Razer USB Sound Car", "Line In (Legacy Device)"],
                ["Microphone (Razer USB Sound Card)"]);

        Assert.Equal(
            ["Microphone (Razer USB Sound Card)", "Line In (Legacy Device)"],
            names);
    }

    [Fact]
    public void Unrelated_core_audio_endpoints_are_not_offered_as_winmm_capture_devices()
    {
        IReadOnlyList<string> names =
            DeepgramSpeechRecognitionService.PreferFullInputDeviceNames(
                ["Microphone (USB Audio)"],
                ["Headset Earphone (Different Device)"]);

        Assert.Equal(["Microphone (USB Audio)"], names);
    }

    [Fact]
    public void Dynamic_keyterms_include_the_personal_phrase_and_current_flight()
    {
        var profile = new SpeechRecognitionProfile
        {
            Keyterms = ["request IFR clearance"],
        };
        var plan = new FlightPlan
        {
            FlightPlanId = "test",
            Callsign = Callsign.Create("ST123"),
            DepartureIcao = "EGKK",
            DestinationIcao = "ENVA",
            DepartureRunway = RunwayIdentifier.Create("26L"),
            StandardInstrumentDeparture = "BOGNA1X",
            RouteLegs = [new RouteLeg { Identifier = "LAM" }],
            SourceIdentifier = "Test",
        };
        AtcSession session = AtcSession.Create(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                SessionMode.Mock,
                AircraftState.Unknown(DateTimeOffset.UtcNow))
            .WithFlightPlan(plan);

        IReadOnlyList<string> terms = DeepgramRecognitionProfile.BuildKeyterms(profile, session);

        Assert.Contains("request IFR clearance", terms);
        Assert.Contains("ST123", terms);
        Assert.Contains("Sierra Tango one two three", terms);
        Assert.Contains("EGKK", terms);
        Assert.Contains("ENVA", terms);
        Assert.Contains("BOGNA1X", terms);
        Assert.Contains("LAM", terms);
    }

    [Fact]
    public void Dynamic_keyterms_include_the_exact_pending_readback_values()
    {
        var profile = new SpeechRecognitionProfile();
        AtcSession session = AtcSession.Create(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                SessionMode.Mock,
                AircraftState.Unknown(DateTimeOffset.UtcNow))
            .WithPendingReadback(new PendingReadback
            {
                ClearanceId = Guid.CreateVersion7(),
                ClearanceType = ClearanceType.IfrClearance,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpectedItems =
                [
                    new ExpectedReadbackItem
                    {
                        Type = ReadbackItemType.InitialAltitude,
                        ExpectedValue = "7000",
                        SpokenValue = "climb initially seven thousand",
                    },
                    new ExpectedReadbackItem
                    {
                        Type = ReadbackItemType.Squawk,
                        ExpectedValue = "5217",
                        SpokenValue = "squawk five two one seven",
                    },
                    new ExpectedReadbackItem
                    {
                        Type = ReadbackItemType.Frequency,
                        ExpectedValue = "121.700",
                        SpokenValue = "departure frequency one two one decimal seven",
                    },
                ],
            });

        IReadOnlyList<string> terms = DeepgramRecognitionProfile.BuildKeyterms(profile, session);

        Assert.Contains("climb initially seven thousand", terms);
        Assert.Contains("squawk five two one seven", terms);
        Assert.Contains("departure frequency one two one decimal seven", terms);
    }

    [Fact]
    public void An_explicit_full_phrase_correction_ignores_provider_punctuation()
    {
        var corrections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sierra Tango 123 requests IFR clearance. Clearance. E c"] =
                "Sierra Tango one two three, request IFR clearance",
        };

        string corrected = DeepgramRecognitionProfile.ApplyCorrections(
            "Sierra Tango 123 requests IFR clearance clearance E c",
            corrections);

        Assert.Equal(
            "Sierra Tango one two three, request IFR clearance",
            corrected);
    }
}
